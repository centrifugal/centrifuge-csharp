using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a subscription to a channel.
    /// </summary>
    public class CentrifugeSubscription : IDisposable
    {
        private readonly CentrifugeClient _client;
        private readonly CentrifugeSubscriptionOptions _options;
        private readonly SemaphoreSlim _stateLock = new SemaphoreSlim(1, 1);
        private readonly object _stateChangeLock = new object();
        private readonly object _deltaLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _readyPromises = new();

        private volatile CentrifugeSubscriptionState _state = CentrifugeSubscriptionState.Unsubscribed;
        private int _resubscribeAttempts;
        private CancellationTokenSource? _resubscribeCts;
        private CentrifugeStreamPosition? _streamPosition;
        // Numeric channel ID assigned by the server when channel compaction is
        // negotiated. Pushes then carry this ID instead of the channel name.
        private long _pushChannelId;
        private bool _deltaNegotiated;
        private byte[]? _prevValue;
        private Timer? _refreshTimer;
        private int _refreshAttempts;
        private bool _refreshRequired;
        private int _promiseId;
        private bool _inflight;
        private int _disposed;
        private int _epoch;

        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the current subscription state.
        /// </summary>
        public CentrifugeSubscriptionState State => _state;

        /// <summary>
        /// Test hook: whether a resubscribe retry is currently armed (scheduled and not
        /// cancelled). Lets integration tests synchronize with the retry pipeline through
        /// state polling instead of wall-clock sleeps.
        /// </summary>
        internal bool HasPendingResubscribe
        {
            get
            {
                lock (_stateChangeLock)
                {
                    return _resubscribeCts != null && !_resubscribeCts.IsCancellationRequested;
                }
            }
        }

        /// <summary>
        /// Event raised when subscription state changes.
        /// </summary>
        public event EventHandler<CentrifugeSubscriptionStateEventArgs>? StateChanged;

        /// <summary>
        /// Event raised when subscription is subscribing.
        /// </summary>
        public event EventHandler<CentrifugeSubscribingEventArgs>? Subscribing;

        /// <summary>
        /// Event raised when subscription is subscribed.
        /// </summary>
        public event EventHandler<CentrifugeSubscribedEventArgs>? Subscribed;

        /// <summary>
        /// Event raised when subscription is unsubscribed.
        /// </summary>
        public event EventHandler<CentrifugeUnsubscribedEventArgs>? Unsubscribed;

        /// <summary>
        /// Event raised when a publication is received.
        /// </summary>
        public event EventHandler<CentrifugePublicationEventArgs>? Publication;

        /// <summary>
        /// Event raised when a join event is received.
        /// </summary>
        public event EventHandler<CentrifugeJoinEventArgs>? Join;

        /// <summary>
        /// Event raised when a leave event is received.
        /// </summary>
        public event EventHandler<CentrifugeLeaveEventArgs>? Leave;

        /// <summary>
        /// Event raised when an error occurs.
        /// </summary>
        public event EventHandler<CentrifugeErrorEventArgs>? Error;

        internal CentrifugeSubscription(CentrifugeClient client, string channel, CentrifugeSubscriptionOptions? options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _options = options ?? new CentrifugeSubscriptionOptions();
            _options.Validate();

            if (_options.Since != null)
            {
                _streamPosition = _options.Since;
            }
        }

        /// <summary>
        /// Subscribes to the channel. This method returns immediately and starts the subscription process in the background.
        /// Use ReadyAsync() to wait for the subscription to be established, or use the Subscribed event.
        /// </summary>
        public void Subscribe()
        {
            ThrowIfDisposed();
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Subscribed || _state == CentrifugeSubscriptionState.Subscribing)
                {
                    return;
                }
                _resubscribeAttempts = 0;
            }
            StartSubscribing();
        }

        private void ThrowIfDisposed()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
                throw new ObjectDisposedException(nameof(CentrifugeSubscription));
        }

        /// <summary>
        /// Unsubscribes from the channel. This method returns immediately and starts the unsubscription process in the background.
        /// </summary>
        public void Unsubscribe()
        {
            _ = SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.UnsubscribeCalled, "unsubscribe called");
        }

        /// <summary>
        /// Returns a Task that completes when the subscription is established.
        /// If already subscribed, the Task completes immediately.
        /// If unsubscribed, the Task is rejected.
        /// </summary>
        /// <param name="timeout">Optional timeout.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes when subscribed.</returns>
        public Task ReadyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs;
            int promiseId;

            // Hold _stateChangeLock across both the state check and the registration so we
            // don't race with HandleSubscribeReply / SetUnsubscribedAsync resolving promises
            // between us reading _state and inserting the tcs into _readyPromises.
            lock (_stateChangeLock)
            {
                switch (_state)
                {
                    case CentrifugeSubscriptionState.Unsubscribed:
                        return Task.FromException(new CentrifugeException(CentrifugeErrorCodes.SubscriptionUnsubscribed, "subscription unsubscribed"));

                    case CentrifugeSubscriptionState.Subscribed:
                        return Task.CompletedTask;
                }

                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                promiseId = NextPromiseId();
                _readyPromises[promiseId] = tcs;
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenRegistration timeoutRegistration = default;
            CancellationTokenRegistration cancellationRegistration = default;

            if (timeout.HasValue)
            {
                timeoutCts = new CancellationTokenSource(timeout.Value);
                timeoutRegistration = timeoutCts.Token.Register(() =>
                {
                    if (_readyPromises.TryRemove(promiseId, out var promise))
                    {
                        promise.TrySetException(new CentrifugeException(CentrifugeErrorCodes.Timeout, "timeout"));
                    }
                });
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    if (_readyPromises.TryRemove(promiseId, out var promise))
                    {
                        promise.TrySetCanceled(cancellationToken);
                    }
                });
            }

            // Dispose registrations when task completes to prevent memory leaks
            tcs.Task.ContinueWith(_ =>
            {
                timeoutRegistration.Dispose();
                cancellationRegistration.Dispose();
                timeoutCts?.Dispose();
            }, TaskContinuationOptions.ExecuteSynchronously);

            return tcs.Task;
        }


        /// <summary>
        /// Sets the subscription data. This will be used for all subsequent subscription attempts.
        /// The data is copied internally to prevent external modifications.
        /// </summary>
        /// <param name="data">New subscription data.</param>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            lock (_stateChangeLock)
            {
                _options.Data = data.IsEmpty ? default : data.ToArray();
            }
        }

        /// <summary>
        /// Sets server-side publication filter based on publication tags.
        /// This allows filtering publications on the server side before they are sent to the client.
        /// The filter is applied on the next subscription/resubscription attempt.
        /// Cannot be used together with delta compression.
        /// </summary>
        /// <param name="tagsFilter">The filter expression, or null to remove filtering.</param>
        /// <exception cref="InvalidOperationException">Thrown when trying to set tags filter while delta compression is enabled.</exception>
        /// <example>
        /// // Simple equality filter
        /// sub.SetTagsFilter(CentrifugeFilterNodeBuilder.Eq("ticker", "BTC"));
        ///
        /// // Complex filter with logical operators
        /// sub.SetTagsFilter(
        ///     CentrifugeFilterNodeBuilder.And(
        ///         CentrifugeFilterNodeBuilder.Eq("ticker", "BTC"),
        ///         CentrifugeFilterNodeBuilder.Gt("price", "50000")
        ///     )
        /// );
        ///
        /// // Filter with IN operator
        /// sub.SetTagsFilter(CentrifugeFilterNodeBuilder.In("ticker", "BTC", "ETH", "SOL"));
        /// </example>
        public void SetTagsFilter(CentrifugeFilterNode? tagsFilter)
        {
            lock (_stateChangeLock)
            {
                if (tagsFilter != null && !string.IsNullOrEmpty(_options.Delta))
                {
                    throw new InvalidOperationException("Cannot use delta and TagsFilter together");
                }
                _options.TagsFilter = tagsFilter;
            }
        }

        /// <summary>
        /// Publishes data to the channel.
        /// Automatically waits for the subscription to be established before publishing.
        /// </summary>
        /// <param name="data">Data to publish.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task PublishAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            // Wait for subscription to be ready with default timeout
            await ReadyAsync(_client.Timeout, cancellationToken).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                Publish = new PublishRequest
                {
                    Channel = Channel,
                    Data = ByteString.CopyFrom(data.Span)
                }
            };

            var reply = await _client.SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

            if (reply.Error != null)
            {
                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }
        }

        /// <summary>
        /// Gets the channel history.
        /// Automatically waits for the subscription to be established before fetching history.
        /// </summary>
        /// <param name="options">History options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>History result.</returns>
        public async Task<CentrifugeHistoryResult> HistoryAsync(CentrifugeHistoryOptions? options = null, CancellationToken cancellationToken = default)
        {
            // Wait for subscription to be ready with default timeout
            await ReadyAsync(_client.Timeout, cancellationToken).ConfigureAwait(false);

            var request = new HistoryRequest
            {
                Channel = Channel
            };

            if (options != null)
            {
                if (options.Limit.HasValue)
                {
                    request.Limit = options.Limit.Value;
                }

                if (options.Since != null)
                {
                    request.Since = new Centrifugal.Centrifuge.Protocol.StreamPosition
                    {
                        Offset = options.Since.Value.Offset,
                        Epoch = options.Since.Value.Epoch
                    };
                }

                request.Reverse = options.Reverse;
            }

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                History = request
            };

            var reply = await _client.SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

            if (reply.Error != null)
            {
                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            var publications = new List<CentrifugePublicationEventArgs>();
            foreach (var pub in reply.History.Publications)
            {
                publications.Add(CentrifugeClient.CreatePublicationArgs(Channel, pub));
            }

            return new CentrifugeHistoryResult(
                publications.ToArray(),
                reply.History.Epoch,
                reply.History.Offset
            );
        }

        /// <summary>
        /// Gets the channel presence.
        /// Automatically waits for the subscription to be established before fetching presence.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence result.</returns>
        public async Task<CentrifugePresenceResult> PresenceAsync(CancellationToken cancellationToken = default)
        {
            // Wait for subscription to be ready with default timeout
            await ReadyAsync(_client.Timeout, cancellationToken).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                Presence = new PresenceRequest
                {
                    Channel = Channel
                }
            };

            var reply = await _client.SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

            if (reply.Error != null)
            {
                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            var clients = new Dictionary<string, CentrifugeClientInfo>();
            foreach (var kvp in reply.Presence.Presence)
            {
                var info = kvp.Value;
                clients[kvp.Key] = new CentrifugeClientInfo(
                    info.User,
                    info.Client,
                    info.ConnInfo.ToByteArray(),
                    info.ChanInfo.ToByteArray()
                );
            }

            return new CentrifugePresenceResult(clients);
        }

        /// <summary>
        /// Gets the channel presence stats.
        /// Automatically waits for the subscription to be established before fetching presence stats.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence stats result.</returns>
        public async Task<CentrifugePresenceStatsResult> PresenceStatsAsync(CancellationToken cancellationToken = default)
        {
            // Wait for subscription to be ready with default timeout
            await ReadyAsync(_client.Timeout, cancellationToken).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                PresenceStats = new PresenceStatsRequest
                {
                    Channel = Channel
                }
            };

            var reply = await _client.SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

            if (reply.Error != null)
            {
                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            return new CentrifugePresenceStatsResult(
                reply.PresenceStats.NumClients,
                reply.PresenceStats.NumUsers
            );
        }

        internal async Task ResubscribeAsync()
        {
            await StartSubscribingAsync(CentrifugeSubscribingCodes.TransportClosed, "transport closed").ConfigureAwait(false);
        }

        /// <summary>
        /// Moves subscription to subscribing state. Used when client connection is lost.
        /// </summary>
        internal void MoveToSubscribing(int code, string reason)
        {
            CentrifugeSubscriptionState prevState;
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed) return;

                if (_state == CentrifugeSubscriptionState.Subscribing)
                {
                    _resubscribeCts?.Cancel();
                    return;
                }

                prevState = SetState(CentrifugeSubscriptionState.Subscribing);
                _resubscribeCts?.Cancel();
            }
            if (prevState != CentrifugeSubscriptionState.Subscribing)
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(prevState, CentrifugeSubscriptionState.Subscribing));
            Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(code, reason));
        }

        private void StartSubscribing()
        {
            CentrifugeSubscriptionState prevState;
            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Unsubscribed)
                {
                    return;
                }
                prevState = SetState(CentrifugeSubscriptionState.Subscribing);
            }
            if (prevState != CentrifugeSubscriptionState.Subscribing)
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(prevState, CentrifugeSubscriptionState.Subscribing));
            Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(CentrifugeSubscribingCodes.SubscribeCalled, "subscribe called"));

            // Schedule subscribe batch; SendSubscribeIfNeededAsync does the authoritative
            // locked state check, so no bare _client.State read needed here.
            _client.ScheduleSubscribeBatch();
        }

        internal async Task SendSubscribeIfNeededAsync()
        {
            // Check if transport is open and subscription is in subscribing state
            if (!_client.TransportIsOpen)
            {
                return;
            }

            // Check state under lock to prevent race conditions with Unsubscribe()
            lock (_stateChangeLock)
            {
                // Check if already inflight or not in subscribing state
                if (_inflight || _state != CentrifugeSubscriptionState.Subscribing)
                {
                    return;
                }

                _inflight = true;
            }

            bool inflightClearedEarly = false;
            try
            {
                var (result, connectionGeneration) = await SendSubscribeCommandAsync().ConfigureAwait(false);
                // Success path: clear _inflight BEFORE HandleSubscribeReply fires the
                // Subscribed event / resolves ReadyAsync. A user handler that calls
                // ResubscribeAsync() in response to Subscribed must not observe a stale
                // _inflight=true and silently no-op.
                lock (_stateChangeLock) { _inflight = false; }
                inflightClearedEarly = true;
                if (result != null && !HandleSubscribeReply(result, connectionGeneration))
                {
                    // Stale reply from a connection that was torn down between the
                    // reply arriving and being processed — discarded. The teardown's
                    // resubscribe sweep may have already run and skipped this sub
                    // (it was still inflight then), so schedule our own retry.
                    await ScheduleResubscribeAsync().ConfigureAwait(false);
                }
            }
            catch (CentrifugeTimeoutException)
            {
                lock (_stateChangeLock) { _inflight = false; }
                inflightClearedEarly = true;
                OnError("subscribe", new CentrifugeException(CentrifugeErrorCodes.Timeout, "subscribe timeout", true));
                await _client.HandleSubscribeTimeoutAsync().ConfigureAwait(false);
            }
            catch (CentrifugeUnauthorizedException)
            {
                lock (_stateChangeLock) { _inflight = false; }
                inflightClearedEarly = true;
                await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
            }
            catch (CentrifugeGetStateException ex)
            {
                OnError("getState", ex);
                lock (_stateChangeLock) { _inflight = false; }
                inflightClearedEarly = true;
                await ScheduleResubscribeAsync().ConfigureAwait(false);
                return;
            }
            catch (CentrifugeException ex)
            {
                if (ex.Code == CentrifugeErrorCodes.UnrecoverablePosition && _options.GetState != null)
                {
                    // Unrecoverable position with GetState: reset position so the next
                    // subscribe attempt calls GetState to reload app state from scratch.
                    // No error event raised — matches other SDKs.
                    lock (_stateChangeLock)
                    {
                        _streamPosition = null;
                        lock (_deltaLock) { _prevValue = null; }
                        _inflight = false;
                    }
                    inflightClearedEarly = true;
                    await ScheduleResubscribeAsync().ConfigureAwait(false);
                    return;
                }
                OnError("subscribe", ex);
                if (ex.Code < 100 || ex.Code == 109 || ex.Temporary)
                {
                    lock (_stateChangeLock)
                    {
                        if (ex.Code == 109) _refreshRequired = true;
                        // Release _inflight BEFORE ScheduleResubscribeAsync so the retry's
                        // inner SendSubscribeIfNeededAsync can re-acquire it. The finally block
                        // must NOT clear it again — that would corrupt a concurrent caller that
                        // acquired _inflight between here and the finally.
                        _inflight = false;
                    }
                    inflightClearedEarly = true;
                    await ScheduleResubscribeAsync().ConfigureAwait(false);
                    return;
                }
                else
                {
                    // Permanent error — release _inflight BEFORE SetUnsubscribedAsync so a
                    // user handler that calls Subscribe() in response to the Unsubscribed
                    // event isn't blocked by our still-held inflight flag.
                    lock (_stateChangeLock) { _inflight = false; }
                    inflightClearedEarly = true;
                    await SetUnsubscribedAsync(ex.Code, ex.Message).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                OnError("subscribe", ex);
                lock (_stateChangeLock) { _inflight = false; }
                inflightClearedEarly = true;
                await ScheduleResubscribeAsync().ConfigureAwait(false);
                return;
            }
            finally
            {
                if (!inflightClearedEarly)
                {
                    lock (_stateChangeLock) { _inflight = false; }
                }
            }
        }

        private async Task StartSubscribingAsync(int code, string reason)
        {
            // State check and transition must be atomic with _stateChangeLock so a concurrent
            // SetUnsubscribedAsync (user's Unsubscribe()) can't be overwritten by this method.
            CentrifugeSubscriptionState prevState;
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed) return;
                prevState = SetState(CentrifugeSubscriptionState.Subscribing);
            }

            if (prevState != CentrifugeSubscriptionState.Subscribing)
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(prevState, CentrifugeSubscriptionState.Subscribing));
            Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(code, reason));

            // SendSubscribeIfNeededAsync does the authoritative locked state check internally;
            // no bare _client.State read needed here.
            await SendSubscribeIfNeededAsync().ConfigureAwait(false);
        }

        private async Task<(SubscribeResult? Result, long ConnectionGeneration)> SendSubscribeCommandAsync()
        {
            // GetState: ask the app for its current state position. Only called when
            // we don't have a saved position (first subscribe or after a position reset
            // due to unrecoverable position error 112). On normal reconnects with a
            // valid saved position we skip GetState and let the server try recovery —
            // GetState is only called again if recovery fails.
            bool needGetState;
            lock (_stateChangeLock)
            {
                needGetState = _options.GetState != null && _streamPosition == null;
            }
            if (needGetState)
            {
                CentrifugeStreamPosition position;
                try
                {
                    position = await _options.GetState!(Channel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new CentrifugeGetStateException(ex);
                }
                lock (_stateChangeLock)
                {
                    if (_state != CentrifugeSubscriptionState.Subscribing) return (null, 0);
                    _streamPosition = position;
                }
            }

            string? token;
            bool needsRefresh;
            lock (_stateChangeLock)
            {
                token = _options.Token;
                needsRefresh = _refreshRequired;
            }

            // If refresh is required or token is empty, try to get a new token
            if ((string.IsNullOrEmpty(token) || needsRefresh) && _options.GetToken != null)
            {
                try
                {
                    token = await _options.GetToken(Channel).ConfigureAwait(false);
                    lock (_stateChangeLock)
                    {
                        if (_state != CentrifugeSubscriptionState.Subscribing) return (null, 0);
                        _options.Token = token;
                    }
                }
                catch (CentrifugeUnauthorizedException)
                {
                    await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
                    return (null, 0);
                }
            }

            ReadOnlyMemory<byte> data;
            CentrifugeFilterNode? tagsFilter;
            lock (_stateChangeLock)
            {
                data = _options.Data;
                tagsFilter = _options.TagsFilter;
            }

            var request = new SubscribeRequest
            {
                Channel = Channel,
                Token = token ?? string.Empty,
                Positioned = _options.Positioned,
                Recoverable = _options.Recoverable,
                JoinLeave = _options.JoinLeave
            };

            CentrifugeStreamPosition? streamPos;
            lock (_stateChangeLock) { streamPos = _streamPosition; }
            if (streamPos != null)
            {
                request.Recover = true;
                request.Epoch = streamPos.Value.Epoch;
                request.Offset = streamPos.Value.Offset;
            }

            if (!data.IsEmpty)
            {
                request.Data = ByteString.CopyFrom(data.Span);
            }

            if (tagsFilter != null)
            {
                request.Tf = tagsFilter.InternalNode;
            }

            if (!string.IsNullOrEmpty(_options.Delta))
            {
                request.Delta = _options.Delta;
            }

            // Always offer channel compaction: when the server supports and allows it,
            // the subscribe result carries a numeric channel ID and subsequent pushes
            // use that ID instead of the string channel name.
            long flag = CentrifugeSubscriptionFlags.ChannelCompaction;
            if (_options.GetState != null)
            {
                // Ask the server to reject the subscribe with error 112 when recovery
                // from the provided position is impossible, instead of returning
                // recovered=false — so we can call GetState again to reload state.
                flag |= CentrifugeSubscriptionFlags.RejectUnrecovered;
            }
            request.Flag = flag;

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                Subscribe = request
            };

            // Capture the connection generation as close to the send as possible: the
            // reply is only applied if the client is still in the same Connected
            // session when it is processed (see HandleSubscribeReply). A teardown
            // sneaking between this capture and the send only causes a benign
            // discard-and-retry of an otherwise valid reply.
            var connectionGeneration = _client.ConnectionGeneration;

            var reply = await _client.SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);

            if (reply.Error != null)
            {
                // Error code 109 means token expired - mark for refresh on next subscribe
                if (reply.Error.Code == 109)
                {
                    lock (_stateChangeLock) { _refreshRequired = true; }
                }

                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            return (reply.Subscribe, connectionGeneration);
        }

        /// <summary>
        /// Update the channel compaction ID registration in the client's push
        /// routing registry. Pass 0 to clear (no compaction / sub gone).
        ///
        /// Always re-registers even when the ID is unchanged: the client drops the
        /// whole registry on transport teardown, and on reconnect the server commonly
        /// assigns the same ID again — the registration must be restored.
        /// </summary>
        // Resets cached subscription state on "state invalidated" (unsubscribe code
        // 2502 or connection disconnect code 3014) so the resubscribe re-syncs:
        // clears the token (and forces a fresh one via GetToken), the fossil delta
        // base (a stale base would corrupt decoding of the first publication), and
        // the channel-compaction ID mapping. The recovery position, when present
        // (recoverable/positioned subscription), is reset to a sentinel epoch ("_")
        // the server can never match — so the resubscribe reply reports
        // WasRecovering=true, Recovered=false, letting the app reload via its
        // existing recovery-failure path; a non-recoverable subscription has no
        // stream position and simply resubscribes. The real epoch/offset are
        // adopted from the subscribe reply.
        internal void InvalidateState()
        {
            lock (_stateChangeLock)
            {
                _options.Token = string.Empty;
                _refreshRequired = true;
                if (_streamPosition != null)
                {
                    _streamPosition = new CentrifugeStreamPosition(0, "_");
                }
                _prevValue = null;
                SetPushChannelId(0);
            }
        }

        private void SetPushChannelId(long id)
        {
            // Field and registry must change together under _stateChangeLock —
            // otherwise a concurrent clear (unsubscribe) and register (subscribe
            // reply) can interleave so that the registry keeps an entry for an
            // unsubscribed subscription. The registry itself is lock-free, so no
            // lock-ordering hazard. The lock is reentrant: callers may already
            // hold it.
            lock (_stateChangeLock)
            {
                var oldId = _pushChannelId;
                if (id == 0 && oldId == 0) return;
                _pushChannelId = id;
                _client.UpdateSubscriptionPushId(this, oldId, id);
            }
        }

        /// <summary>
        /// Applies a subscribe reply. Returns false when the reply was produced by a
        /// connection that has been torn down since the command was sent (stale) and
        /// was therefore discarded — the caller should schedule a resubscribe, because
        /// the teardown's resubscribe sweep may have already run and skipped this
        /// subscription while the reply was still inflight. Internal for tests.
        /// </summary>
        internal bool HandleSubscribeReply(SubscribeResult result, long connectionGeneration)
        {
            // Reply arrived after Dispose() — drop it to avoid creating a Timer/CTS that
            // never gets cleaned up. Nothing to retry on a disposed subscription.
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return true;

            bool recovered = result.Recovered;

            // Hold _stateChangeLock across the unsubscribed-check, the state transition
            // and ResolvePromises so a ReadyAsync caller can't observe Subscribing,
            // miss our resolve, and then register a tcs that nobody will complete.
            bool wasRecovering;
            CentrifugeStreamPosition? streamPositionSnapshot;
            CentrifugeSubscriptionState prevState;
            lock (_stateChangeLock)
            {
                wasRecovering = _streamPosition != null;
                if (_state == CentrifugeSubscriptionState.Unsubscribed)
                {
                    // Subscription was unsubscribed during subscribe, ignore the reply.
                    // No retry — unsubscribed is a deliberate terminal state here.
                    return true;
                }

                // Stale-reply guard: the client left the Connected session this reply
                // belongs to (transport closed / no ping / disconnect) after the reply
                // arrived but before it was processed. Applying it would flip the
                // subscription to Subscribed while the client is reconnecting, and the
                // post-reconnect resubscribe sweep would then skip it — stranding the
                // subscription without a server-side counterpart. The generation is
                // bumped under the client's state lock before subscriptions are moved
                // to subscribing, so observing a matching generation here guarantees
                // the teardown has not started touching subscription state yet.
                if (connectionGeneration != _client.ConnectionGeneration)
                {
                    return false;
                }

                // Server returns stream position when subscription is positioned OR
                // recoverable — track it in both cases so recovery on reconnect works
                // for recoverable-only channels too.
                if (result.Positioned || result.Recoverable)
                {
                    _streamPosition = new CentrifugeStreamPosition(result.Offset, result.Epoch);
                }

                // Re-negotiate delta state for this subscribe session. The previous session's
                // _prevValue MUST be cleared: the server starts a fresh delta chain on every
                // subscribe reply (its first publication is a full snapshot), so applying a
                // delta against the prior session's bytes would corrupt the payload.
                // Take _deltaLock so concurrent ApplyDeltaIfNeeded callers see the new
                // value through the same lock that guards the delta state.
                lock (_deltaLock)
                {
                    _deltaNegotiated = result.Delta;
                    _prevValue = null;
                }

                // Clear refresh timer and reset attempts
                ClearRefreshTimer();
                _refreshAttempts = 0;
                _refreshRequired = false;

                // Schedule token refresh if token expires
                if (result.Expires)
                {
                    ScheduleTokenRefresh(result.Ttl);
                }

                prevState = SetState(CentrifugeSubscriptionState.Subscribed);
                _resubscribeAttempts = 0;

                // Channel compaction: register the numeric channel ID assigned by
                // the server (0 when not negotiated — also clears a stale ID from a
                // previous subscribe session). Must happen inside this critical
                // section: it shares the lock with the unsubscribed-check above, so
                // a concurrent unsubscribe either prevents the registration or runs
                // its own clear strictly after it.
                SetPushChannelId(result.Id);

                // Capture stream position under lock so the Subscribed event sees a consistent snapshot.
                streamPositionSnapshot = _streamPosition;

                // Resolve ready promises
                ResolvePromises();
            }

            if (prevState != CentrifugeSubscriptionState.Subscribed)
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(prevState, CentrifugeSubscriptionState.Subscribed));

            Subscribed?.Invoke(this, new CentrifugeSubscribedEventArgs(
                wasRecovering,
                recovered,
                result.Recoverable,
                result.Positioned,
                streamPositionSnapshot,
                result.Data.ToByteArray()
            ));

            // Dispatch recovered publications.
            // Isolate handler exceptions: a single throwing Publication handler must not
            // abort the recovery loop (would drop remaining publications and skip the
            // _streamPosition advance, mis-sequencing future live publications).
            foreach (var pub in result.Publications)
            {
                var pubArgs = ApplyDeltaIfNeeded(pub);
                try
                {
                    Publication?.Invoke(this, pubArgs);
                }
                catch (Exception ex)
                {
                    OnError("publication", ex);
                }

                if ((result.Positioned || result.Recoverable) && pub.Offset > 0)
                {
                    lock (_stateChangeLock)
                    {
                        if (_streamPosition == null || pub.Offset > _streamPosition.Value.Offset)
                            _streamPosition = new CentrifugeStreamPosition(pub.Offset, result.Epoch);
                    }
                }
            }

            return true;
        }

        internal void HandlePublication(Publication pub)
        {
            var pubArgs = ApplyDeltaIfNeeded(pub);
            Publication?.Invoke(this, pubArgs);

            if (pub.Offset > 0)
            {
                lock (_stateChangeLock)
                {
                    if (_streamPosition != null && pub.Offset > _streamPosition.Value.Offset)
                        _streamPosition = new CentrifugeStreamPosition(pub.Offset, _streamPosition.Value.Epoch);
                }
            }
        }

        private CentrifugePublicationEventArgs ApplyDeltaIfNeeded(Publication pub)
        {
            var data = pub.Data.ToByteArray();

            // Hold _deltaLock (not _stateChangeLock) across the full read-apply-write sequence so concurrent callers
            // (HandleSubscribeReply recovery loop + HandlePublication live stream) can't
            // both read the same _prevValue and then corrupt the delta chain with two
            // independent writes. A dedicated lock keeps O(payload-size) Fossil decode
            // off the hot state-change path.
            if (!string.IsNullOrEmpty(_options.Delta))
            {
                lock (_deltaLock)
                {
                    if (_deltaNegotiated)
                    {
                        if (_prevValue != null && data.Length > 0)
                            data = Fossil.ApplyDelta(_prevValue, data);
                        _prevValue = data;
                    }
                }
            }

            return CentrifugeClient.CreatePublicationArgs(Channel, pub, data);
        }

        internal void HandleJoin(Join join)
        {
            if (join.Info == null) return;

            var info = new CentrifugeClientInfo(
                join.Info.User,
                join.Info.Client,
                join.Info.ConnInfo.ToByteArray(),
                join.Info.ChanInfo.ToByteArray()
            );

            Join?.Invoke(this, new CentrifugeJoinEventArgs(Channel, info));
        }

        internal void HandleLeave(Leave leave)
        {
            if (leave.Info == null) return;

            var info = new CentrifugeClientInfo(
                leave.Info.User,
                leave.Info.Client,
                leave.Info.ConnInfo.ToByteArray(),
                leave.Info.ChanInfo.ToByteArray()
            );

            Leave?.Invoke(this, new CentrifugeLeaveEventArgs(Channel, info));
        }

        private async Task ScheduleResubscribeAsync()
        {
            CancellationToken delayToken;
            int currentAttempts;
            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Subscribing) return;
                // Don't recreate _resubscribeCts after Dispose has nulled it — that would leak the CTS.
                if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;

                var oldCts = _resubscribeCts;
                _resubscribeCts = new CancellationTokenSource();
                oldCts?.Cancel();
                oldCts?.Dispose();
                delayToken = _resubscribeCts.Token;
                currentAttempts = _resubscribeAttempts++;
            }

            int delay = Utilities.CalculateBackoff(
                currentAttempts,
                _options.MinResubscribeDelay,
                _options.MaxResubscribeDelay
            );

            try
            {
                await Task.Delay(delay, delayToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Subscribing) return;
            }

            await SendSubscribeIfNeededAsync().ConfigureAwait(false);
        }

        internal async Task SetUnsubscribedAsync(int code, string reason)
        {
            // Change state synchronously first
            CentrifugeSubscriptionState prevState;
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed)
                {
                    return;
                }

                prevState = SetState(CentrifugeSubscriptionState.Unsubscribed);

                // Reject ready promises
                RejectPromises(new CentrifugeException(CentrifugeErrorCodes.SubscriptionUnsubscribed, "subscription unsubscribed"));
            }

            // Channel compaction ID is no longer valid once unsubscribed.
            SetPushChannelId(0);

            // Fire events outside the lock so re-entrant SDK calls in handlers don't deadlock.
            if (prevState != CentrifugeSubscriptionState.Unsubscribed)
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(prevState, CentrifugeSubscriptionState.Unsubscribed));
            Unsubscribed?.Invoke(this, new CentrifugeUnsubscribedEventArgs(code, reason));

            // Now do async cleanup without holding locks.
            // Defense-in-depth: catch ObjectDisposedException because Dispose() may have
            // disposed _stateLock between our state-change above and now.
            try
            {
                await _stateLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                // Cancel/clear under _stateChangeLock — ScheduleResubscribeAsync and
                // HandleRefreshError/etc. access these same fields under _stateChangeLock.
                lock (_stateChangeLock)
                {
                    _resubscribeCts?.Cancel();
                    ClearRefreshTimer();
                }

                try
                {
                    var cmd = new Command
                    {
                        Id = _client.NextCommandId(),
                        Unsubscribe = new UnsubscribeRequest
                        {
                            Channel = Channel
                        }
                    };

                    await _client.SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);
                }
                catch (CentrifugeException ex) when (
                    ex.Code == CentrifugeErrorCodes.ClientDisconnected ||
                    ex.Code == CentrifugeErrorCodes.ConnectionClosed)
                {
                    // Client was not connected — skip reconnect trigger
                }
                catch
                {
                    await _client.HandleUnsubscribeErrorAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try { _stateLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private CentrifugeSubscriptionState SetState(CentrifugeSubscriptionState newState)
        {
            var oldState = _state;
            _state = newState;
            if (oldState != newState) _epoch++;
            return oldState;
        }

        private void OnError(string type, Exception exception)
        {
            Error?.Invoke(this, new CentrifugeErrorEventArgs(type, 0, exception.Message, false, exception));
        }

        private void ScheduleTokenRefresh(uint ttl)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            // ttl=0 means "no expiry given" — skip scheduling rather than busy-loop.
            if (ttl == 0) return;
            var delay = Utilities.TtlToMilliseconds(ttl);
            _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        internal async Task RefreshTokenAsync()
        {
            int epochSnapshot;
            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Subscribed || _options.GetToken == null) return;
                epochSnapshot = _epoch;
            }

            try
            {
                var token = await _options.GetToken(Channel).ConfigureAwait(false);
                lock (_stateChangeLock)
                {
                    // Discard the token if the subscription left/re-entered Subscribed during the await —
                    // the token belongs to a prior session.
                    if (_state != CentrifugeSubscriptionState.Subscribed || _epoch != epochSnapshot) return;
                    _options.Token = token;
                }

                var cmd = new Command
                {
                    Id = _client.NextCommandId(),
                    SubRefresh = new SubRefreshRequest
                    {
                        Channel = Channel,
                        Token = token
                    }
                };

                var reply = await _client.SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);

                if (reply.Error != null)
                {
                    HandleRefreshError(new CentrifugeException(
                        (int)reply.Error.Code,
                        reply.Error.Message,
                        reply.Error.Temporary
                    ));
                }
                else
                {
                    HandleRefreshReply(reply.SubRefresh);
                }
            }
            catch (CentrifugeUnauthorizedException)
            {
                // Token refresh unauthorized - unsubscribe
                await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError("refresh", ex);
                lock (_stateChangeLock)
                {
                    if (_state != CentrifugeSubscriptionState.Subscribed) return;
                    var delay = Utilities.CalculateBackoff(_refreshAttempts, _options.MinResubscribeDelay, _options.MaxResubscribeDelay);
                    _refreshAttempts++;
                    ClearRefreshTimer();
                    _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
                }
            }
        }

        private void HandleRefreshReply(SubRefreshResult result)
        {
            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Subscribed) return;
                _refreshAttempts = 0;
                if (result.Expires) ScheduleTokenRefresh(result.Ttl);
            }
        }

        private void HandleRefreshError(CentrifugeException error)
        {
            OnError("refresh", error);

            if (error.Temporary)
            {
                lock (_stateChangeLock)
                {
                    if (_state != CentrifugeSubscriptionState.Subscribed) return;
                    var delay = Utilities.CalculateBackoff(_refreshAttempts, _options.MinResubscribeDelay, _options.MaxResubscribeDelay);
                    _refreshAttempts++;
                    ClearRefreshTimer();
                    _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
                }
            }
            else
            {
                _ = SetUnsubscribedAsync(error.Code, error.Message);
            }
        }

        private int NextPromiseId()
        {
            return Interlocked.Increment(ref _promiseId);
        }

        private void ResolvePromises()
        {
            foreach (var kvp in _readyPromises)
            {
                if (_readyPromises.TryRemove(kvp.Key, out var promise))
                {
                    promise.TrySetResult(true);
                }
            }
        }

        private void RejectPromises(CentrifugeException error)
        {
            foreach (var kvp in _readyPromises)
            {
                if (_readyPromises.TryRemove(kvp.Key, out var promise))
                {
                    promise.TrySetException(error);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            CancellationTokenSource? cts;
            Timer? timer;
            lock (_stateChangeLock)
            {
                cts = _resubscribeCts;
                _resubscribeCts = null;
                timer = _refreshTimer;
                _refreshTimer = null;
            }

            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            cts?.Dispose();
            timer?.Dispose();
            _stateLock.Dispose();
        }
    }
}
