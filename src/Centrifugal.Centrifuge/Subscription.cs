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
    public class CentrifugeSubscription
    {
        private readonly CentrifugeClient _client;
        private readonly CentrifugeSubscriptionOptions _options;
        private readonly SemaphoreSlim _stateLock = new SemaphoreSlim(1, 1);
        private readonly object _stateChangeLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _readyPromises = new();

        private CentrifugeSubscriptionState _state = CentrifugeSubscriptionState.Unsubscribed;
        private int _resubscribeAttempts;
        private CancellationTokenSource? _resubscribeCts;
        private CentrifugeStreamPosition? _streamPosition;
        private bool _deltaNegotiated;
        private byte[]? _prevValue;
        private Timer? _refreshTimer;
        private int _refreshAttempts;
        private bool _refreshRequired;
        private int _promiseId;
        private bool _inflight;

        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the current subscription state.
        /// </summary>
        public CentrifugeSubscriptionState State => _state;

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
            if (_state == CentrifugeSubscriptionState.Subscribed)
            {
                return;
            }

            if (_state == CentrifugeSubscriptionState.Subscribing)
            {
                return;
            }

            _resubscribeAttempts = 0;
            StartSubscribing();
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
        /// <returns>A task that completes when subscribed.</returns>
        public Task ReadyAsync(TimeSpan? timeout = null)
        {
            switch (_state)
            {
                case CentrifugeSubscriptionState.Unsubscribed:
                    return Task.FromException(new CentrifugeException(CentrifugeErrorCodes.SubscriptionUnsubscribed, "subscription unsubscribed"));

                case CentrifugeSubscriptionState.Subscribed:
                    return Task.CompletedTask;

                default:
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var promiseId = NextPromiseId();
                    _readyPromises[promiseId] = tcs;

                    if (timeout.HasValue)
                    {
                        var cts = new CancellationTokenSource(timeout.Value);
                        cts.Token.Register(() =>
                        {
                            if (_readyPromises.TryRemove(promiseId, out var promise))
                            {
                                promise.TrySetException(new CentrifugeException(CentrifugeErrorCodes.Timeout, "timeout"));
                            }
                        });
                    }

                    return tcs.Task;
            }
        }


        /// <summary>
        /// Sets the subscription data. This will be used for all subsequent subscription attempts.
        /// The data is copied internally to prevent external modifications.
        /// </summary>
        /// <param name="data">New subscription data.</param>
        public void SetData(byte[]? data)
        {
            lock (_stateChangeLock)
            {
                _options.Data = data != null ? (byte[])data.Clone() : null;
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
        public async Task PublishAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            // Wait for subscription to be ready
            await ReadyAsync().ConfigureAwait(false);

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                Publish = new PublishRequest
                {
                    Channel = Channel,
                    Data = ByteString.CopyFrom(data)
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
            // Wait for subscription to be ready
            await ReadyAsync().ConfigureAwait(false);

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
                        Offset = options.Since.Offset,
                        Epoch = options.Since.Epoch
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
            // Wait for subscription to be ready
            await ReadyAsync().ConfigureAwait(false);

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
            // Wait for subscription to be ready
            await ReadyAsync().ConfigureAwait(false);

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
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed)
                {
                    return;
                }

                await StartSubscribingAsync(CentrifugeSubscribingCodes.TransportClosed, "transport closed").ConfigureAwait(false);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Moves subscription to subscribing state. Used when client connection is lost.
        /// </summary>
        internal void MoveToSubscribing(int code, string reason)
        {
            lock (_stateChangeLock)
            {
                if (_state != CentrifugeSubscriptionState.Subscribed)
                {
                    return;
                }

                SetState(CentrifugeSubscriptionState.Subscribing);
                Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(code, reason));
            }
        }

        private void StartSubscribing()
        {
            SetState(CentrifugeSubscriptionState.Subscribing);
            Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(CentrifugeSubscribingCodes.SubscribeCalled, "subscribe called"));

            // If not connected, the subscription will be sent when connection is established
            if (_client.State != CentrifugeClientState.Connected)
            {
                return;
            }

            // If connected, schedule subscribe command to be sent in a batch
            // This allows multiple subscribe calls to be automatically batched together
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

            // Check state again after releasing lock - it could have changed to Unsubscribed
            // This prevents sending subscribe command if Unsubscribe() was called
            if (_state != CentrifugeSubscriptionState.Subscribing)
            {
                lock (_stateChangeLock)
                {
                    _inflight = false;
                }
                return;
            }

            try
            {
                await SendSubscribeCommandAsync().ConfigureAwait(false);
            }
            catch (CentrifugeTimeoutException)
            {
                // Subscribe timeout should trigger client reconnect, just like in centrifuge-js
                OnError("subscribe", new CentrifugeException(CentrifugeErrorCodes.Timeout, "subscribe timeout", true));
                // Trigger client disconnect with reconnect
                await _client.HandleSubscribeTimeoutAsync().ConfigureAwait(false);
            }
            catch (CentrifugeUnauthorizedException)
            {
                await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError("subscribe", ex);
                await ScheduleResubscribeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_stateChangeLock)
                {
                    _inflight = false;
                }
            }
        }

        private async Task StartSubscribingAsync(int code, string reason)
        {
            SetState(CentrifugeSubscriptionState.Subscribing);
            Subscribing?.Invoke(this, new CentrifugeSubscribingEventArgs(code, reason));

            if (_client.State != CentrifugeClientState.Connected)
            {
                // Wait for client to connect
                return;
            }

            try
            {
                await SendSubscribeCommandAsync().ConfigureAwait(false);
            }
            catch (CentrifugeTimeoutException)
            {
                // Subscribe timeout should trigger client reconnect, just like in centrifuge-js
                OnError("subscribe", new CentrifugeException(CentrifugeErrorCodes.Timeout, "subscribe timeout", true));
                // Trigger client disconnect with reconnect
                await _client.HandleSubscribeTimeoutAsync().ConfigureAwait(false);
            }
            catch (CentrifugeUnauthorizedException)
            {
                await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError("subscribe", ex);
                await ScheduleResubscribeAsync().ConfigureAwait(false);
            }
        }

        private async Task SendSubscribeCommandAsync()
        {
            string? token;
            lock (_stateChangeLock)
            {
                token = _options.Token;
            }

            // If refresh is required or token is empty, try to get a new token
            if ((string.IsNullOrEmpty(token) || _refreshRequired) && _options.GetToken != null)
            {
                try
                {
                    token = await _options.GetToken(Channel).ConfigureAwait(false);
                    lock (_stateChangeLock)
                    {
                        _options.Token = token;
                    }
                }
                catch (CentrifugeUnauthorizedException)
                {
                    await SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized").ConfigureAwait(false);
                    return;
                }
            }

            byte[]? data;
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

            if (_streamPosition != null)
            {
                request.Recover = true;
                request.Epoch = _streamPosition.Epoch;
                request.Offset = _streamPosition.Offset;
            }

            if (data != null)
            {
                request.Data = ByteString.CopyFrom(data);
            }

            if (tagsFilter != null)
            {
                request.Tf = tagsFilter.InternalNode;
            }

            if (!string.IsNullOrEmpty(_options.Delta))
            {
                request.Delta = _options.Delta;
            }

            var cmd = new Command
            {
                Id = _client.NextCommandId(),
                Subscribe = request
            };

            var reply = await _client.SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);

            if (reply.Error != null)
            {
                // Error code 109 means token expired - mark for refresh on next subscribe
                if (reply.Error.Code == 109)
                {
                    _refreshRequired = true;
                }

                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            HandleSubscribeReply(reply.Subscribe);
        }

        private void HandleSubscribeReply(SubscribeResult result)
        {
            // Check if subscription was unsubscribed while subscribe was in-flight
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed)
                {
                    // Subscription was unsubscribed during subscribe, ignore the reply
                    return;
                }
            }

            bool wasRecovering = _streamPosition != null;
            bool recovered = result.Recovered;

            if (result.Positioned)
            {
                _streamPosition = new CentrifugeStreamPosition(result.Offset, result.Epoch);
            }

            // Check if delta compression was negotiated with server
            if (result.Delta)
            {
                _deltaNegotiated = true;
            }
            else
            {
                _deltaNegotiated = false;
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

            SetState(CentrifugeSubscriptionState.Subscribed);

            // Resolve ready promises
            ResolvePromises();

            Subscribed?.Invoke(this, new CentrifugeSubscribedEventArgs(
                wasRecovering,
                recovered,
                result.Recoverable,
                result.Positioned,
                _streamPosition,
                result.Data.ToByteArray()
            ));

            _resubscribeAttempts = 0;

            // Dispatch recovered publications
            foreach (var pub in result.Publications)
            {
                var pubArgs = ApplyDeltaIfNeeded(pub);
                Publication?.Invoke(this, pubArgs);

                if (result.Positioned && pub.Offset > 0)
                {
                    _streamPosition = new CentrifugeStreamPosition(pub.Offset, result.Epoch);
                }
            }
        }

        internal void HandlePublication(Publication pub)
        {
            var pubArgs = ApplyDeltaIfNeeded(pub);
            Publication?.Invoke(this, pubArgs);

            if (pub.Offset > 0 && _streamPosition != null)
            {
                _streamPosition = new CentrifugeStreamPosition(pub.Offset, _streamPosition.Epoch);
            }
        }

        private CentrifugePublicationEventArgs ApplyDeltaIfNeeded(Publication pub)
        {
            var data = pub.Data.ToByteArray();

            // Apply delta decompression if negotiated with server
            if (!string.IsNullOrEmpty(_options.Delta) && _deltaNegotiated)
            {
                try
                {
                    if (_prevValue != null && data.Length > 0)
                    {
                        // Apply fossil delta to get the actual publication data
                        data = Fossil.ApplyDelta(_prevValue, data);
                    }
                    _prevValue = data;
                }
                catch (Exception ex)
                {
                    OnError("delta", ex);
                    // Fall through to use original data on delta error
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
            if (_state != CentrifugeSubscriptionState.Subscribing)
            {
                return;
            }

            _resubscribeCts?.Cancel();
            _resubscribeCts = new CancellationTokenSource();

            int delay = Utilities.CalculateBackoff(
                _resubscribeAttempts++,
                _options.MinResubscribeDelay,
                _options.MaxResubscribeDelay
            );

            try
            {
                await Task.Delay(delay, _resubscribeCts.Token).ConfigureAwait(false);
                await SendSubscribeCommandAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Resubscribe was cancelled
            }
        }

        internal async Task SetUnsubscribedAsync(int code, string reason)
        {
            // Change state synchronously first
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeSubscriptionState.Unsubscribed)
                {
                    return;
                }

                SetState(CentrifugeSubscriptionState.Unsubscribed);

                // Reject ready promises
                RejectPromises(new CentrifugeException(CentrifugeErrorCodes.SubscriptionUnsubscribed, "subscription unsubscribed"));

                Unsubscribed?.Invoke(this, new CentrifugeUnsubscribedEventArgs(code, reason));
            }

            // Now do async cleanup without holding locks
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _resubscribeCts?.Cancel();
                ClearRefreshTimer();

                if (_client.State == CentrifugeClientState.Connected)
                {
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
                    catch
                    {
                        // Ignore errors during unsubscribe
                    }
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void SetState(CentrifugeSubscriptionState newState)
        {
            var oldState = _state;
            _state = newState;

            if (oldState != newState)
            {
                StateChanged?.Invoke(this, new CentrifugeSubscriptionStateEventArgs(oldState, newState));
            }
        }

        private void OnError(string type, Exception exception)
        {
            Error?.Invoke(this, new CentrifugeErrorEventArgs(type, 0, exception.Message, false, exception));
        }

        private void ScheduleTokenRefresh(uint ttl)
        {
            _refreshTimer?.Dispose();
            var delay = Utilities.TtlToMilliseconds(ttl);
            _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        private async Task RefreshTokenAsync()
        {
            if (_state != CentrifugeSubscriptionState.Subscribed || _options.GetToken == null)
            {
                return;
            }

            try
            {
                var token = await _options.GetToken(Channel).ConfigureAwait(false);
                lock (_stateChangeLock)
                {
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
                // Network or temporary error - retry with exponential backoff
                OnError("refresh", ex);
                var delay = Utilities.CalculateBackoff(
                    _refreshAttempts,
                    _options.MinResubscribeDelay,
                    _options.MaxResubscribeDelay
                );
                _refreshAttempts++;
                _refreshTimer?.Dispose();
                _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
            }
        }

        private void HandleRefreshReply(SubRefreshResult result)
        {
            _refreshAttempts = 0;

            // Schedule next refresh if token still expires
            if (result.Expires)
            {
                ScheduleTokenRefresh(result.Ttl);
            }
        }

        private void HandleRefreshError(CentrifugeException error)
        {
            OnError("refresh", error);

            // For temporary errors, retry with exponential backoff
            if (error.Temporary)
            {
                var delay = Utilities.CalculateBackoff(
                    _refreshAttempts,
                    _options.MinResubscribeDelay,
                    _options.MaxResubscribeDelay
                );
                _refreshAttempts++;
                _refreshTimer?.Dispose();
                _refreshTimer = new Timer(_ => _ = RefreshTokenAsync(), null, delay, Timeout.Infinite);
            }
            else
            {
                // Permanent error - unsubscribe
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
    }
}
