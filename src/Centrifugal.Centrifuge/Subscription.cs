using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a subscription to a channel. Its state changes on the event loop of its client (see
    /// <see cref="CentrifugeClient"/>).
    /// </summary>
    public class CentrifugeSubscription : IDisposable
    {
        private readonly CentrifugeClient _client;
        private readonly CentrifugeSubscriptionOptions _options;
        private volatile CentrifugeSubscriptionState _state = CentrifugeSubscriptionState.Unsubscribed;
        /// <summary>Removed from the client (disposed): it can't subscribe again.</summary>
        private volatile bool _removed;

        // Everything below belongs to the client's loop.

        private string _token;
        private ReadOnlyMemory<byte> _data;
        private CentrifugeFilterNode? _tagsFilter;
        /// <summary>The next subscribe recovers from the position.</summary>
        private bool _recover;
        /// <summary>The stream position: the last publication the app received, or the position a subscribe
        /// reply, Since or GetState gave; null — none (GetState is asked for one).</summary>
        private ulong? _offset;
        private string? _epoch;
        /// <summary>The offset of the publication being dispatched, until its handler returned.</summary>
        private ulong? _pendingOffset;
        /// <summary>The channel id of channel compaction, scoped to the subscribed period.</summary>
        private long _id;
        private int _resubscribeAttempts;
        private LoopTimer? _resubscribeTimer;
        private LoopTimer? _refreshTimer;
        private bool _deltaNegotiated;
        private byte[]? _prevValue;
        /// <summary>A subscribe flow is in progress: a callback or a subscribe command.</summary>
        private bool _inflight;
        /// <summary>The subscribe flow: replies and callbacks of an earlier one are ignored. Every unsubscribe
        /// starts a new one.</summary>
        private int _subscribeFlow;
        /// <summary>Incremented whenever the subscription stops being subscribed: a token refresh of an
        /// earlier subscribed period is outdated.</summary>
        private int _refreshGeneration;
        /// <summary>Incremented by every state change: an event of a change a handler followed with another
        /// one is outdated.</summary>
        private int _stateTransitions;
        private readonly Waiters _promises;

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
        /// Event raised when subscription is subscribed. Recovered publications follow it, before any
        /// publication received after the subscribe reply.
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
        /// Event raised when an error occurs. An exception thrown by a handler of another event of the
        /// subscription is reported here too, with the event's name as the type.
        /// </summary>
        public event EventHandler<CentrifugeErrorEventArgs>? Error;

        internal CentrifugeSubscription(CentrifugeClient client, string channel, CentrifugeSubscriptionOptions? options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _options = options?.Clone() ?? new CentrifugeSubscriptionOptions();
            _options.Validate();
            _token = _options.Token ?? string.Empty;
            _data = _options.Data.IsEmpty ? default : _options.Data.ToArray();
            _tagsFilter = _options.TagsFilter;
            _promises = new Waiters(client.Loop);

            if (_options.Since is { } since)
            {
                _offset = since.Offset;
                _epoch = since.Epoch;
                _recover = true;
            }
            if (_options.GetState != null)
            {
                _recover = true;
            }
        }

        private EventLoop Loop => _client.Loop;

        /// <summary>
        /// Subscribes to the channel. Returns at once; the subscription is established in the background. Use
        /// <see cref="ReadyAsync"/> or the <see cref="Subscribed"/> event to know when it is.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The subscription was disposed (removed from the client).</exception>
        public void Subscribe()
        {
            if (_removed) throw new ObjectDisposedException(nameof(CentrifugeSubscription), $"Subscription to the channel {Channel} was removed from the client");
            Loop.Invoke(() =>
            {
                if (_removed || _state == CentrifugeSubscriptionState.Subscribed) return;
                // A subscribe in progress keeps its backoff.
                if (_state == CentrifugeSubscriptionState.Unsubscribed) _resubscribeAttempts = 0;
                SetSubscribing(CentrifugeSubscribingCodes.SubscribeCalled, "subscribe called");
            });
        }

        /// <summary>
        /// Unsubscribes from the channel, keeping the stream position: a later <see cref="Subscribe"/> recovers
        /// from it. The unsubscribe reply isn't awaited: an error reply or its timeout reconnects the client.
        /// </summary>
        public void Unsubscribe()
        {
            Loop.Invoke(() => SetUnsubscribed(CentrifugeUnsubscribedCodes.UnsubscribeCalled, "unsubscribe called", true));
        }

        /// <summary>
        /// Waits until the subscription is subscribed. Completes immediately if it already is; fails if the
        /// subscription is unsubscribed, or becomes unsubscribed before subscribing.
        /// </summary>
        /// <param name="timeout">Optional timeout.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task completing on subscribe, or failing with <see cref="CentrifugeTimeoutException"/>
        /// once <paramref name="timeout"/> passes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The timeout is negative but not infinite, or exceeds what a timer supports.</exception>
        public Task ReadyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Utilities.ValidateWaitTimeout(timeout, nameof(timeout));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Decided at once: completed at once, so a handler blocking on it doesn't wait for the step it holds.
            Loop.Invoke(() => WaitSubscribed(() => Loop.Complete(() => ready.TrySetResult(true)), ex => Loop.Complete(() => Waiters.Fail(ready, ex)),
                timeout is { } t && t != Timeout.InfiniteTimeSpan ? (int)t.TotalMilliseconds : null, cancellationToken,
                () => ready.TrySetResult(true), ex => Waiters.Fail(ready, ex)));
            return ready.Task;
        }

        /// <summary>Proceeds at once when subscribed, else when it subscribes within the timeout; fails when
        /// unsubscribed. Call in a step.</summary>
        private void WaitSubscribed(Action proceed, Action<Exception> fail, int? timeoutMs, CancellationToken cancellationToken,
            Action? proceedNow = null, Action<Exception>? failNow = null)
        {
            switch (_state)
            {
                case CentrifugeSubscriptionState.Subscribed:
                    (proceedNow ?? proceed)();
                    return;
                case CentrifugeSubscriptionState.Unsubscribed:
                    (failNow ?? fail)(UnsubscribedError());
                    return;
                default:
                    _promises.Add(proceed, fail, timeoutMs, cancellationToken);
                    return;
            }
        }

        /// <summary>
        /// Sets the subscription data, used by the next subscribe. The data is copied.
        /// </summary>
        /// <param name="data">New subscription data.</param>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            var copy = data.IsEmpty ? default : data.ToArray();
            Loop.Invoke(() => _data = copy);
        }

        /// <summary>
        /// Sets server-side publication filter based on publication tags, used by the next subscribe. Cannot
        /// be used together with delta compression.
        /// </summary>
        /// <param name="tagsFilter">The filter expression, or null to remove filtering.</param>
        /// <exception cref="InvalidOperationException">Delta compression is enabled.</exception>
        /// <example>
        /// sub.SetTagsFilter(CentrifugeFilterNodeBuilder.Eq("ticker", "BTC"));
        /// </example>
        public void SetTagsFilter(CentrifugeFilterNode? tagsFilter)
        {
            if (tagsFilter != null && !string.IsNullOrEmpty(_options.Delta))
            {
                throw new InvalidOperationException("Cannot use delta and TagsFilter together");
            }
            Loop.Invoke(() => _tagsFilter = tagsFilter);
        }

        /// <summary>
        /// Publishes data to the channel. If the subscription is not subscribed yet, waits for that first,
        /// within the client timeout.
        /// </summary>
        /// <param name="data">Data to publish.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task PublishAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await MethodCallAsync(cancellationToken).ConfigureAwait(false);
            await _client.CallAsync(new Command
            {
                Publish = new PublishRequest
                {
                    Channel = Channel,
                    Data = ByteString.CopyFrom(data.Span)
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the channel history. If the subscription is not subscribed yet, waits for that first, within
        /// the client timeout.
        /// </summary>
        /// <param name="options">History options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>History result.</returns>
        public async Task<CentrifugeHistoryResult> HistoryAsync(CentrifugeHistoryOptions? options = null, CancellationToken cancellationToken = default)
        {
            await MethodCallAsync(cancellationToken).ConfigureAwait(false);
            var reply = await _client.CallAsync(new Command
            {
                History = CentrifugeClient.HistoryRequestOf(Channel, options)
            }, cancellationToken).ConfigureAwait(false);
            return CentrifugeClient.HistoryResultOf(Channel, reply.History);
        }

        /// <summary>
        /// Gets the channel presence. If the subscription is not subscribed yet, waits for that first, within
        /// the client timeout.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence result.</returns>
        public async Task<CentrifugePresenceResult> PresenceAsync(CancellationToken cancellationToken = default)
        {
            await MethodCallAsync(cancellationToken).ConfigureAwait(false);
            return await _client.PresenceAsync(Channel, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the channel presence stats. If the subscription is not subscribed yet, waits for that first,
        /// within the client timeout.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence stats result.</returns>
        public async Task<CentrifugePresenceStatsResult> PresenceStatsAsync(CancellationToken cancellationToken = default)
        {
            await MethodCallAsync(cancellationToken).ConfigureAwait(false);
            return await _client.PresenceStatsAsync(Channel, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Waits for the subscription to be subscribed, up to the client timeout (as centrifuge-js
        /// _methodCall of a subscription).</summary>
        private Task MethodCallAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Decided at once: completed at once, so a handler blocking on it doesn't wait for the step it holds.
            Loop.Invoke(() => WaitSubscribed(() => Loop.Complete(() => ready.TrySetResult(true)), ex => Loop.Complete(() => Waiters.Fail(ready, ex)),
                _client.TimeoutMilliseconds, cancellationToken,
                () => ready.TrySetResult(true), ex => Waiters.Fail(ready, ex)));
            return ready.Task;
        }

        /// <summary>
        /// Unsubscribes (as <see cref="Unsubscribe"/>) and removes the subscription from its client: the channel
        /// is free for a new subscription at once, and this one can't subscribe again. A handler of its
        /// Unsubscribed event may subscribe the channel anew.
        /// </summary>
        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            _client.Unregister(this);
            Loop.Invoke(() => SetUnsubscribed(CentrifugeUnsubscribedCodes.UnsubscribeCalled, "unsubscribe called", true));
        }

        /// <summary>The client is disposed: unsubscribed with ClientClosed, nothing sent. Call in a step.</summary>
        internal void CloseForClient()
        {
            _removed = true;
            SetUnsubscribed(CentrifugeUnsubscribedCodes.ClientClosed, "client closed", false);
        }

        // ---------------------------------------------------------------------------------------------
        // Steps (centrifuge-js BaseSubscription): all run on the client's loop.

        private bool SetState(CentrifugeSubscriptionState newState)
        {
            if (_state == newState) return false;
            var oldState = _state;
            _state = newState;
            _stateTransitions++;
            Raise(StateChanged, new CentrifugeSubscriptionStateEventArgs(oldState, newState), "stateChanged");
            return true;
        }

        private bool UsesToken => _token.Length > 0 || _options.GetToken != null;

        private void ClearSubscribingState()
        {
            _resubscribeAttempts = 0;
            ClearResubscribeTimer();
        }

        private void ClearSubscribedState()
        {
            ClearRefreshTimer();
            // A token refresh in progress belongs to the subscribed period that ends here.
            _refreshGeneration++;
        }

        /// <summary>
        /// "State invalidated": unsubscribe code 2502, or disconnect code 3014. The token is dropped when
        /// GetToken can get another (a static one stays), and the delta base too. A position is made
        /// unrecoverable with the sentinel epoch "_" (an empty epoch would mean any epoch to the server): the
        /// next subscribe reports WasRecovering and not Recovered, and the app reloads.
        /// </summary>
        internal void InvalidateState()
        {
            if (_options.GetToken != null) _token = string.Empty;
            _prevValue = null;
            if (_offset != null)
            {
                _offset = 0;
                _epoch = "_";
            }
        }

        /// <summary>The connection was lost (or a temporary unsubscribe push): to subscribing.</summary>
        internal void MoveToSubscribing(int code, string reason) => SetSubscribing(code, reason);

        private void SetSubscribing(int code, string reason)
        {
            if (_state == CentrifugeSubscriptionState.Subscribing) return;
            if (_state == CentrifugeSubscriptionState.Subscribed) ClearSubscribedState();
            // The channel id is scoped to the subscribed period.
            SetId(0);
            // Not when a StateChanged handler changed the state again: the event would come after that
            // change's own events.
            var transition = _stateTransitions + 1;
            if (SetState(CentrifugeSubscriptionState.Subscribing) && _stateTransitions == transition)
            {
                Raise(Subscribing, new CentrifugeSubscribingEventArgs(code, reason), "subscribing");
            }
            if (_state != CentrifugeSubscriptionState.Subscribing) return;
            DoSubscribe();
        }

        /// <summary>The client is connected (or its WebSocket opened): subscribes unless a flow is in progress
        /// or a resubscribe backoff runs — an error is retried after its backoff, also across a reconnect.</summary>
        internal void SubscribeIfIdle()
        {
            if (_inflight || _resubscribeTimer != null) return;
            if (_state == CentrifugeSubscriptionState.Subscribing) DoSubscribe();
        }

        private void DoSubscribe()
        {
            // Delayed until the transport is open: the client sends it then.
            if (!_client.TransportIsOpen) return;
            if (_inflight) return;
            _inflight = true;

            var flow = _subscribeFlow;

            // GetState only without a position: on a first subscribe or after an unrecoverable position (112).
            // A resubscribe with a position tries to recover first.
            if (_options.GetState != null && _offset == null)
            {
                LoadStreamState(flow);
                return;
            }

            if (!UsesToken || _token.Length > 0)
            {
                SendSubscribe(_token);
                return;
            }

            GetSubscriptionToken(token => HandleTokenResponse(token, flow), error => HandleTokenError(error, flow));
        }

        private bool FlowOutdated(int flow) => flow != _subscribeFlow;

        private void LoadStreamState(int flow)
        {
            var getState = _options.GetState!;
            Callbacks.Run(Loop, () => getState(Channel), position =>
            {
                if (FlowOutdated(flow)) return;
                if (_state != CentrifugeSubscriptionState.Subscribing)
                {
                    _inflight = false;
                    return;
                }
                _offset = position.Offset;
                _epoch = position.Epoch;
                _recover = true;

                if (!UsesToken || _token.Length > 0)
                {
                    SendSubscribe(_token);
                }
                else
                {
                    GetSubscriptionToken(token => HandleTokenResponse(token, flow), error => HandleTokenError(error, flow));
                }
            }, error =>
            {
                if (FlowOutdated(flow)) return;
                if (_state != CentrifugeSubscriptionState.Subscribing)
                {
                    _inflight = false;
                    return;
                }
                _inflight = false;
                // A temporary error (as centrifuge-js), reported with its own type.
                if (_client.IsConnected)
                    EmitError("getState", new CentrifugeGetStateException(error), ScheduleResubscribe);
                else
                    ScheduleResubscribe();
            });
        }

        /// <summary>Gets a subscription token from GetToken, off the loop. Without GetToken: a configuration
        /// error, then unauthorized.</summary>
        private void GetSubscriptionToken(Action<string?> onToken, Action<Exception> onError)
        {
            var getToken = _options.GetToken;
            if (getToken == null)
            {
                EmitErrorEvent("configuration", new CentrifugeConfigurationException("provide a function to get channel subscription token"));
                Loop.Defer(() => onError(new CentrifugeUnauthorizedException()));
                return;
            }
            Callbacks.Run(Loop, () => getToken(Channel), onToken, onError);
        }

        private void HandleTokenResponse(string? token, int flow)
        {
            if (FlowOutdated(flow)) return;
            if (_state != CentrifugeSubscriptionState.Subscribing)
            {
                _inflight = false;
                return;
            }
            if (string.IsNullOrEmpty(token))
            {
                _inflight = false;
                FailUnauthorized();
                return;
            }
            _token = token!;
            SendSubscribe(_token);
        }

        private void HandleTokenError(Exception error, int flow)
        {
            if (FlowOutdated(flow)) return;
            if (_state != CentrifugeSubscriptionState.Subscribing)
            {
                _inflight = false;
                return;
            }
            if (error is CentrifugeUnauthorizedException)
            {
                _inflight = false;
                FailUnauthorized();
                return;
            }
            EmitError("subscribeToken", new CentrifugeException(CentrifugeErrorCodes.SubscriptionSubscribeToken, error.Message, true, error), () =>
            {
                _inflight = false;
                ScheduleResubscribe();
            });
        }

        private void SendSubscribe(string token)
        {
            if (!_client.TransportIsOpen)
            {
                _inflight = false;
                return;
            }
            var command = BuildSubscribeCommand(token);
            var flow = _subscribeFlow;
            _client.RegisterCall(command, reply =>
            {
                // The app unsubscribed meanwhile, maybe to subscribe again.
                if (FlowOutdated(flow)) return;
                _inflight = false;
                if (_state != CentrifugeSubscriptionState.Subscribing) return;
                if (reply.Subscribe == null)
                {
                    // A reply breaking the protocol: retried as a temporary error.
                    EmitError("subscribe", new System.IO.InvalidDataException("subscribe reply without result"), ScheduleResubscribe);
                    return;
                }
                SetSubscribed(reply.Subscribe);
            }, (error, _) =>
            {
                if (FlowOutdated(flow)) return;
                _inflight = false;
                HandleSubscribeError(error);
            });
            _client.AddCommand(command);
        }

        private Command BuildSubscribeCommand(string token)
        {
            var request = new SubscribeRequest
            {
                Channel = Channel,
                Token = token,
                Positioned = _options.Positioned,
                Recoverable = _options.Recoverable,
                JoinLeave = _options.JoinLeave
            };

            // Channel compaction is always offered; with GetState the server rejects an unrecoverable position
            // with 112 instead of subscribing with recovered=false, so the state is loaded again.
            long flag = CentrifugeSubscriptionFlags.ChannelCompaction;
            if (_options.GetState != null) flag |= CentrifugeSubscriptionFlags.RejectUnrecovered;
            request.Flag = flag;

            if (_recover)
            {
                request.Recover = true;
                // A publication being dispatched counts as delivered: a subscribe built from its handler
                // recovers after it.
                request.Offset = _pendingOffset ?? _offset ?? 0;
                request.Epoch = _epoch ?? string.Empty;
            }

            if (!_data.IsEmpty) request.Data = ByteString.CopyFrom(_data.Span);
            if (!string.IsNullOrEmpty(_options.Delta)) request.Delta = _options.Delta;
            if (_tagsFilter != null) request.Tf = _tagsFilter.InternalNode;

            return new Command { Subscribe = request };
        }

        private void HandleSubscribeError(CentrifugeException error)
        {
            if (_state != CentrifugeSubscriptionState.Subscribing) return;
            if (error.Code == CentrifugeErrorCodes.ConnectionClosed)
            {
                // The transport closed with the subscribe in flight: not an error of the subscribe, the next
                // connection subscribes again without a backoff.
                return;
            }
            if (error.Code == CentrifugeErrorCodes.Timeout)
            {
                // The server state is unknown: the client reconnects.
                _client.DisconnectInternal(CentrifugeConnectingCodes.SubscribeTimeout, "subscribe timeout", true);
                return;
            }
            if (error.Code == CentrifugeErrorCodes.UnrecoverablePosition && _options.GetState != null)
            {
                // The next subscribe loads the state again.
                _offset = null;
                _epoch = null;
                _recover = false;
                _prevValue = null;
                ScheduleResubscribe();
                return;
            }
            SubscribeError(error);
        }

        private void SetSubscribed(SubscribeResult result)
        {
            if (_state != CentrifugeSubscriptionState.Subscribing) return;

            ClearSubscribingState();

            if (result.Id != 0) SetId(result.Id);
            _deltaNegotiated = result.Delta;
            // The server's first publication after a subscribe is full: the delta chain starts over.
            _prevValue = null;

            SetState(CentrifugeSubscriptionState.Subscribed);
            // A StateChanged handler may have unsubscribed already: no Subscribed then, and the position stays
            // where it was — the app wasn't told about a failed recovery (Recovered false), so the next
            // subscribe must not recover from past the gap (centrifuge-js stores it before the transition).
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            if (result.Recoverable)
            {
                _recover = true;
                // After a recovery the server replies with the position recovered from: each recovered
                // publication delivered below moves it.
                _offset = result.Offset;
                _epoch = result.Epoch;
            }
            else
            {
                // A channel the server doesn't recover is no channel to recover from: otherwise a position from
                // Since or GetState asks to recover on every resubscribe, and the app is told every time that a
                // recovery failed. The flag comes from every reply, so recovery enabled later is picked up.
                _recover = false;
            }
            Raise(Subscribed, new CentrifugeSubscribedEventArgs(
                result.WasRecovering,
                result.Recovered,
                result.Recoverable,
                result.Positioned,
                result.Positioned || result.Recoverable ? new CentrifugeStreamPosition(result.Offset, result.Epoch) : (CentrifugeStreamPosition?)null,
                result.Data.ToByteArray()
            ), "subscribed");
            // A Subscribed handler may have unsubscribed, and subscribed again: waiters made since then wait
            // for that subscribe.
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            _promises.ResolveAll();

            // A publication handler may unsubscribe: the rest isn't delivered and doesn't move the position.
            foreach (var pub in result.Publications)
            {
                if (_state != CentrifugeSubscriptionState.Subscribed) break;
                if (!Deliver(pub)) return;
            }

            if (result.Expires && _state == CentrifugeSubscriptionState.Subscribed)
            {
                ScheduleRefresh(Utilities.TtlToMilliseconds(result.Ttl));
            }
        }

        /// <summary>
        /// Switches the subscription to Unsubscribed. An unsubscribe command is sent only if
        /// <paramref name="sendUnsubscribe"/> is set and the server may know the subscription, i.e. it is
        /// subscribed or a subscribe request is in flight.
        /// </summary>
        private void SetUnsubscribed(int code, string reason, bool sendUnsubscribe)
        {
            if (_state == CentrifugeSubscriptionState.Unsubscribed) return;
            if (_state == CentrifugeSubscriptionState.Subscribed)
            {
                if (sendUnsubscribe) _client.SendUnsubscribe(Channel);
                ClearSubscribedState();
            }
            else if (_state == CentrifugeSubscriptionState.Subscribing)
            {
                if (_inflight && sendUnsubscribe) _client.SendUnsubscribe(Channel);
                ClearSubscribingState();
            }
            _inflight = false;
            // Replies and callbacks of a subscribe in progress must not complete a later one.
            _subscribeFlow++;
            SetId(0);
            // Waiters fail before the events: a handler subscribing again makes waiters of the new subscribe.
            _promises.RejectAll(UnsubscribedError());
            // Not when a StateChanged handler changed the state again.
            var transition = _stateTransitions + 1;
            if (SetState(CentrifugeSubscriptionState.Unsubscribed) && _stateTransitions == transition)
            {
                Raise(Unsubscribed, new CentrifugeUnsubscribedEventArgs(code, reason), "unsubscribed");
            }
        }

        private void FailUnauthorized() =>
            SetUnsubscribed(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized", true);

        private void SetId(long id)
        {
            if (_id == id) return;
            _client.SetSubscriptionId(this, _id, id);
            _id = id;
        }

        /// <summary>A live publication; the client routes one only to a subscribed subscription.</summary>
        internal void HandlePublication(Publication pub)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            Deliver(pub);
        }

        /// <summary>
        /// Hands a publication to the app. The position moves after the app has it, so the position it can
        /// observe never runs ahead of what it received; a subscribe built from its handler recovers after it.
        /// A publication that fails to decode is a corrupt stream: the client disconnects. Returns false then.
        /// </summary>
        private bool Deliver(Publication pub)
        {
            byte[] data = pub.Data.ToByteArray();
            if (!string.IsNullOrEmpty(_options.Delta) && _deltaNegotiated)
            {
                try
                {
                    if (pub.Delta)
                        data = Fossil.ApplyDelta(_prevValue ?? throw new InvalidOperationException("delta publication without a base"), data);
                    _prevValue = data;
                }
                catch (Exception ex)
                {
                    EmitErrorEvent("publicationDecode", ex);
                    _client.HandleUndecodablePublication(ex.Message);
                    return false;
                }
            }

            var args = CentrifugeClient.CreatePublicationArgs(Channel, pub, data);
            _pendingOffset = pub.Offset > 0 ? pub.Offset : (ulong?)null;
            bool failed;
            try
            {
                failed = Raise(Publication, args, "publication");
            }
            finally
            {
                _pendingOffset = null;
            }
            // A publication whose handler threw isn't delivered: the position stays before it, so the next
            // recovery delivers it again. The client stopped: nothing after it is delivered either.
            if (failed) return false;
            if (pub.Offset > 0) _offset = pub.Offset;
            // The epoch of a channel that had no stream at subscribe time comes with its first publication.
            if (!string.IsNullOrEmpty(pub.Epoch)) _epoch = pub.Epoch;
            return true;
        }

        internal void HandleJoin(Join join)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            if (join.Info != null)
                Raise(Join, new CentrifugeJoinEventArgs(Channel, CentrifugeClientInfo.FromProtocol(join.Info)), "join");
        }

        internal void HandleLeave(Leave leave)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            if (leave.Info != null)
                Raise(Leave, new CentrifugeLeaveEventArgs(Channel, CentrifugeClientInfo.FromProtocol(leave.Info)), "leave");
        }

        /// <summary>
        /// An unsubscribe push. It applies to a subscribed subscription only: one for a subscription not
        /// subscribed ends an earlier one (the app unsubscribed, or raced unsubscribe and subscribe) — the
        /// server sends it after the reply of a subscribe in progress. A code below 2500 unsubscribes, others
        /// resubscribe (2502 after invalidating the state).
        /// </summary>
        internal void HandleUnsubscribePush(int code, string reason)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            if (code < 2500)
            {
                SetUnsubscribed(code, reason, false);
                return;
            }
            if (code == CentrifugeUnsubscribedCodes.StateInvalidated) InvalidateState();
            SetSubscribing(code, reason);
        }

        private void ScheduleResubscribe()
        {
            if (_state != CentrifugeSubscriptionState.Subscribing) return;
            var delay = Utilities.CalculateBackoff(_resubscribeAttempts, _options.MinResubscribeDelay, _options.MaxResubscribeDelay);
            _resubscribeAttempts++;
            // One timer at a time.
            ClearResubscribeTimer();
            _resubscribeTimer = LoopTimer.Start(Loop, delay, () =>
            {
                _resubscribeTimer = null;
                if (_state == CentrifugeSubscriptionState.Subscribing) DoSubscribe();
            });
        }

        /// <summary>Emits an error event, then runs the continuation unless a handler unsubscribed (maybe to
        /// subscribe again: the continuation must not touch that subscribe).</summary>
        private void EmitError(string type, Exception error, Action continuation)
        {
            var flow = _subscribeFlow;
            EmitErrorEvent(type, error);
            if (flow == _subscribeFlow) continuation();
        }

        private void SubscribeError(CentrifugeException error)
        {
            if (_state != CentrifugeSubscriptionState.Subscribing) return;
            if (error.Code == 109 && _options.GetToken == null)
            {
                // The token expired and there is no GetToken to get another one: unauthorized, as a connection
                // (instead of subscribing again without a token).
                EmitErrorEvent("configuration", new CentrifugeConfigurationException("subscription token expired but no GetToken function set"));
                SetUnsubscribed(CentrifugeUnsubscribedCodes.Unauthorized, "unauthorized", false);
                return;
            }
            if (error.Code < 100 || error.Code == 109 || error.Temporary)
            {
                if (error.Code == 109) _token = string.Empty;
                if (_client.IsConnected)
                    EmitError("subscribe", error, ScheduleResubscribe);
                else
                    ScheduleResubscribe();
            }
            else
            {
                SetUnsubscribed(error.Code, error.Message, false);
            }
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Cancel();
            _refreshTimer = null;
        }

        private void ClearResubscribeTimer()
        {
            _resubscribeTimer?.Cancel();
            _resubscribeTimer = null;
        }

        private void ScheduleRefresh(int delayMs)
        {
            ClearRefreshTimer();
            _refreshTimer = LoopTimer.Start(Loop, delayMs, () =>
            {
                _refreshTimer = null;
                Refresh();
            });
        }

        private void Refresh()
        {
            ClearRefreshTimer();
            // The subscription may end while the token is loading, and a new one start: this refresh is
            // outdated then.
            var generation = _refreshGeneration;
            GetSubscriptionToken(token =>
            {
                if (generation != _refreshGeneration || _state != CentrifugeSubscriptionState.Subscribed) return;
                if (string.IsNullOrEmpty(token))
                {
                    FailUnauthorized();
                    return;
                }
                _token = token!;
                var command = new Command { SubRefresh = new SubRefreshRequest { Channel = Channel, Token = _token } };
                // A reply of the refresh of an earlier subscribed period is outdated.
                _client.RegisterCall(command, reply =>
                {
                    if (generation != _refreshGeneration) return;
                    if (reply.SubRefresh != null) RefreshResponse(reply.SubRefresh);
                    // A reply breaking the protocol: retried as a temporary error.
                    else RefreshError(new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "sub refresh reply without result", true));
                }, (error, _) =>
                {
                    if (generation == _refreshGeneration) RefreshError(error);
                });
                _client.AddCommand(command);
            }, error =>
            {
                if (generation != _refreshGeneration || _state != CentrifugeSubscriptionState.Subscribed) return;
                if (error is CentrifugeUnauthorizedException)
                {
                    FailUnauthorized();
                    return;
                }
                EmitError("refreshToken", new CentrifugeException(CentrifugeErrorCodes.SubscriptionRefreshToken, error.Message, true, error), () =>
                {
                    // A handler may have ended the subscription, e.g. by disconnecting the client.
                    if (generation != _refreshGeneration || _state != CentrifugeSubscriptionState.Subscribed) return;
                    ScheduleRefresh(GetRefreshRetryDelay());
                });
            });
        }

        private void RefreshResponse(SubRefreshResult result)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            ClearRefreshTimer();
            if (result.Expires) ScheduleRefresh(Utilities.TtlToMilliseconds(result.Ttl));
        }

        private void RefreshError(CentrifugeException error)
        {
            if (_state != CentrifugeSubscriptionState.Subscribed) return;
            if (error.Code < 100 || error.Temporary)
            {
                EmitError("refresh", error, () =>
                {
                    if (_state != CentrifugeSubscriptionState.Subscribed) return;
                    ScheduleRefresh(GetRefreshRetryDelay());
                });
            }
            else
            {
                SetUnsubscribed(error.Code, error.Message, true);
            }
        }

        private static int GetRefreshRetryDelay() =>
            Utilities.CalculateBackoff(0, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        // Test hooks: what a server push or a timer does.

        /// <summary>A temporary server unsubscribe of a subscribed subscription (code 2502 invalidates the state).</summary>
        internal void Resubscribe(int code, string reason) =>
            Loop.Invoke(() =>
            {
                if (_state != CentrifugeSubscriptionState.Subscribed) return;
                if (code == CentrifugeUnsubscribedCodes.StateInvalidated) InvalidateState();
                SetSubscribing(code, reason);
            });

        /// <summary>The token refresh timer of a subscribed subscription.</summary>
        internal void RefreshToken() =>
            Loop.Invoke(() =>
            {
                if (_state == CentrifugeSubscriptionState.Subscribed) Refresh();
            });

        /// <summary>A resubscribe backoff runs.</summary>
        internal bool HasPendingResubscribe => Volatile.Read(ref _resubscribeTimer) != null;

        private static CentrifugeException UnsubscribedError() =>
            new CentrifugeException(CentrifugeErrorCodes.SubscriptionUnsubscribed, "subscription unsubscribed", false);

        /// <summary>
        /// Raises one of this object's events through <see cref="EventDispatch.Raise{TArgs}"/>. Returns
        /// whether a handler threw, which stopped the client (see CentrifugeClient.HandlerFailed).
        /// </summary>
        private bool Raise<TArgs>(EventHandler<TArgs>? handler, TArgs args, string type) =>
            _client.HandlerFailed(EventDispatch.Raise(this, handler, args, type, Error, _client.Logger));

        private void EmitErrorEvent(string type, Exception exception) =>
            _client.HandlerFailed(EventDispatch.RaiseError(this, Error, type, exception, _client.Logger));
    }
}
