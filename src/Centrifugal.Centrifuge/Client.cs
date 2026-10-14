using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Centrifuge client for real-time messaging with Centrifugo server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state of the client and its subscriptions changes on one event loop (see the README): an
    /// application call, a frame received, a timer or the result of a callback is one step, and steps run one
    /// at a time, in order. Event handlers run inside the step that raises them, so the application observes
    /// the events in the order of the steps — on the thread the step runs on (the caller's, a transport's or
    /// the thread pool's), one handler at a time.
    /// </para>
    /// <para>
    /// A call into the SDK from a handler takes effect at once, as in centrifuge-js. A call from another
    /// thread while a step runs takes effect right after it: the SDK never blocks the caller. Don't block a
    /// handler on an SDK task (<c>.Wait()</c>, <c>.Result</c>): its completion needs the loop the handler
    /// holds.
    /// </para>
    /// </remarks>
    public class CentrifugeClient : IDisposable, IAsyncDisposable
    {
        /// <summary>Maximum size of a frame of commands (15KB): a batch reaching it is written at once, more
        /// commands go in further frames.</summary>
        private const int MaxCommandBatchSize = 15 * 1024;

        /// <summary>Commands queued within this window go out together, in one frame (or emulation request).</summary>
        private const int CommandBatchDelayMs = 1;

        /// <summary>A call or no-ping timer running at least this late means the process was suspended or
        /// blocked: the reply (or the ping) waiting to be read gets <see cref="LateTimerGraceMs"/> (as centrifuge-js).</summary>
        private const int LateTimerMs = 1000;
        private const int LateTimerGraceMs = 1000;

        private readonly string? _endpoint;
        /// <summary>Transport type derived from the scheme of <see cref="_endpoint"/>.</summary>
        private readonly CentrifugeTransportType _endpointTransport;
        /// <summary>Endpoints to try in order (fallback mode), or null with a single endpoint.</summary>
        private readonly List<CentrifugeTransportEndpoint>? _transportEndpoints;
        private readonly CentrifugeClientOptions _options;
        private readonly ILogger? _logger;
#if NET6_0_OR_GREATER
        private static volatile Microsoft.JSInterop.IJSRuntime? _globalJSRuntime;
        private readonly Microsoft.JSInterop.IJSRuntime? _jsRuntime;
#endif

        /// <summary>The subscriptions, by channel. Changed by any thread (NewSubscription, removal), read by the loop.</summary>
        private readonly ConcurrentDictionary<string, CentrifugeSubscription> _subscriptions = new();
        private volatile int _disposed;
        private volatile CentrifugeClientState _state = CentrifugeClientState.Disconnected;

        // Everything below belongs to the loop: read and written in its steps only.

        private bool _transportIsOpen;
        private int _currentTransportIndex;
        private bool _triedAllTransports;
        /// <summary>A transport proved to reach the server: fallback mode stops moving to the next endpoint
        /// (as centrifuge-js).</summary>
        private bool _transportWasOpen;
        private ITransport? _transport;
        private TransportWriter? _writer;
        private CancellationTokenSource? _openCts;
        private string? _emulationEndpoint;
        private int _transportId;
        private bool _transportClosed = true;
        private bool _reconnecting;
        private LoopTimer? _reconnectTimer;
        private LoopTimer? _connectTimer;
        private int _reconnectAttempts;
        private string? _clientId;
        /// <summary>Incremented on every connect: a token refresh of an earlier connection is outdated (the
        /// client id may repeat across connections).</summary>
        private int _connection;
        private string _session = string.Empty;
        private string _node = string.Empty;
        private readonly Dictionary<string, ServerSubscription> _serverSubs = new();
        /// <summary>The offset of the server-side publication being dispatched, per channel, until its handler
        /// returned: a connect command built from that handler recovers after it.</summary>
        private readonly Dictionary<string, ulong> _pendingServerSubOffsets = new();
        /// <summary>Client-side subscriptions by the channel id of channel compaction.</summary>
        private readonly Dictionary<long, CentrifugeSubscription> _subscriptionsById = new();
        private int _commandId;
        private List<(Command Command, TaskCompletionSource<bool>? Sent)> _commands = new();
        private int _commandsSize;
        /// <summary>A command queued must go out when the step ends (connect, pong), with the others queued.</summary>
        private bool _flushNow;
        private LoopTimer? _flushTimer;
        private bool _refreshRequired;
        private LoopTimer? _refreshTimer;
        private readonly Dictionary<uint, PendingCall> _calls = new();
        private string _token;
        private ReadOnlyMemory<byte> _data;
        private Dictionary<string, string>? _headers;
        private uint _serverPing;
        private LoopTimer? _serverPingTimer;
        private bool _sendPong;
        private readonly Waiters _promises;
        private int _stateTransitions;
        private int _disconnects;
        private int _abortedTransportId;
        private bool _reconnectAttemptsResetPending;
        private Task _lastTransportClose = Task.CompletedTask;

        /// <summary>The loop the state of this client and its subscriptions changes on.</summary>
        internal EventLoop Loop { get; }

        /// <summary>
        /// Gets the current client state.
        /// </summary>
        public CentrifugeClientState State => _state;

        /// <summary>
        /// Event raised when client state changes.
        /// </summary>
        /// <remarks>
        /// Raised first for every transition, before the event of the new state. Keep handlers fast: they run on
        /// the client's loop. Never block on SDK tasks in a handler (<c>.Wait()</c>, <c>.Result</c>) — use
        /// <c>async void</c> and <c>await</c>.
        /// </remarks>
        public event EventHandler<CentrifugeStateEventArgs>? StateChanged;

        /// <summary>
        /// Event raised when client is connecting or reconnecting.
        /// </summary>
        public event EventHandler<CentrifugeConnectingEventArgs>? Connecting;

        /// <summary>
        /// Event raised when client successfully connects to the server.
        /// </summary>
        /// <remarks>
        /// <para>Calls made from a handler (<c>await sub.PublishAsync(...)</c> in an <c>async void</c> handler) are fine;
        /// never block on them with <c>.Wait()</c> or <c>.Result</c>: the reply is processed by the loop the handler holds.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var sub = client.NewSubscription("chat");
        /// sub.Subscribe();
        /// client.Connected += async (sender, e) => {
        ///     await sub.ReadyAsync();
        ///     await sub.PublishAsync(Encoding.UTF8.GetBytes("Hello!"));
        /// };
        /// </code>
        /// </example>
        public event EventHandler<CentrifugeConnectedEventArgs>? Connected;

        /// <summary>
        /// Event raised when client is disconnected from the server.
        /// </summary>
        public event EventHandler<CentrifugeDisconnectedEventArgs>? Disconnected;

        /// <summary>
        /// Event raised when an error occurs, mostly for logging. An exception thrown by a handler of another
        /// event is reported here too, with the event's name as the type.
        /// </summary>
        public event EventHandler<CentrifugeErrorEventArgs>? Error;

        /// <summary>
        /// Event raised when a message is received from server.
        /// </summary>
        public event EventHandler<CentrifugeMessageEventArgs>? Message;

        /// <summary>
        /// Event raised for server-side subscription publications.
        /// </summary>
        public event EventHandler<CentrifugePublicationEventArgs>? Publication;

        /// <summary>
        /// Event raised for server-side subscription join events.
        /// </summary>
        public event EventHandler<CentrifugeJoinEventArgs>? Join;

        /// <summary>
        /// Event raised for server-side subscription leave events.
        /// </summary>
        public event EventHandler<CentrifugeLeaveEventArgs>? Leave;

        /// <summary>
        /// Event raised when a server-side subscription is subscribing: when the connection it was subscribed
        /// in is lost (as centrifuge-js); the next connect subscribes it again, or unsubscribes it.
        /// </summary>
        public event EventHandler<CentrifugeServerSubscribingEventArgs>? ServerSubscribing;

        /// <summary>
        /// Event raised when server-side subscription is subscribed.
        /// </summary>
        public event EventHandler<CentrifugeServerSubscribedEventArgs>? ServerSubscribed;

        /// <summary>
        /// Event raised when server-side subscription is unsubscribed.
        /// </summary>
        public event EventHandler<CentrifugeServerUnsubscribedEventArgs>? ServerUnsubscribed;

#if NET6_0_OR_GREATER
        /// <summary>
        /// Initializes browser interop for Blazor WebAssembly support.
        /// Call this once at application startup (e.g., in Program.cs) to enable browser-native transports without passing IJSRuntime to every client constructor.
        /// </summary>
        /// <param name="jsRuntime">The IJSRuntime instance to use for all clients.</param>
        public static void InitializeBrowserInterop(Microsoft.JSInterop.IJSRuntime jsRuntime)
        {
            _globalJSRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        }
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeClient"/> class. The transport follows
        /// from the URL scheme: WebSocket for ws/wss, HTTP streaming for http/https.
        /// </summary>
        /// <param name="endpoint">WebSocket or HTTP streaming endpoint URL.</param>
        /// <param name="options">Client options.</param>
        /// <exception cref="ArgumentException">The endpoint is not an absolute URL with a ws, wss, http or https scheme.</exception>
        public CentrifugeClient(string endpoint, CentrifugeClientOptions? options = null)
            : this(options)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            _endpoint = endpoint;
            _endpointTransport = TransportOf(endpoint, nameof(endpoint));
            if (_endpointTransport == CentrifugeTransportType.HttpStream) ValidateEmulationEndpoint();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeClient"/> class with multi-transport fallback.
        /// The client keeps its own copy of the endpoints, so modifying them afterwards has no effect. In the
        /// browser the URLs are handed to fetch/WebSocket unchanged, and relative ones resolve against the page.
        /// </summary>
        /// <param name="transportEndpoints">Array of transport endpoints to try in order.</param>
        /// <param name="options">Client options.</param>
        /// <exception cref="ArgumentException">The array is empty, or (outside the browser) an endpoint is not an
        /// absolute URL with a scheme matching its transport type.</exception>
        public CentrifugeClient(CentrifugeTransportEndpoint[] transportEndpoints, CentrifugeClientOptions? options = null)
            : this(options)
        {
            if (transportEndpoints == null || transportEndpoints.Length == 0)
            {
                throw new ArgumentException("Transport endpoints cannot be null or empty", nameof(transportEndpoints));
            }

            _transportEndpoints = new List<CentrifugeTransportEndpoint>(transportEndpoints.Length);
            foreach (var endpoint in transportEndpoints)
            {
                if (endpoint == null || (!IsBrowser && TransportOf(endpoint.Endpoint, nameof(transportEndpoints)) != endpoint.Transport))
                {
                    throw new ArgumentException($"Endpoint '{endpoint?.Endpoint}' doesn't match its transport", nameof(transportEndpoints));
                }
                _transportEndpoints.Add(new CentrifugeTransportEndpoint(endpoint.Transport, endpoint.Endpoint));
            }
            if (_transportEndpoints.Exists(endpoint => endpoint.Transport == CentrifugeTransportType.HttpStream))
                ValidateEmulationEndpoint();
        }

        /// <summary>
        /// Takes a copy of the options. In the browser without an IJSRuntime no transport can be created,
        /// so this fails right away instead of leaving the client retrying forever.
        /// </summary>
        private CentrifugeClient(CentrifugeClientOptions? options)
        {
            _options = options?.Clone() ?? new CentrifugeClientOptions();
            _options.Validate();
            _token = _options.Token ?? string.Empty;
            _data = _options.Data.IsEmpty ? default : _options.Data.ToArray();
            _headers = _options.Headers != null ? new Dictionary<string, string>(_options.Headers) : null;
            _logger = GuardedLogger.Wrap(_options.Logger);
            Loop = new EventLoop(_logger, OnStepEnd);
            _promises = new Waiters(Loop);
#if NET6_0_OR_GREATER
            _jsRuntime = _options.JSRuntime ?? _globalJSRuntime;
            if (_jsRuntime == null && OperatingSystem.IsBrowser())
            {
                throw new CentrifugeConfigurationException(
                    "Running in browser environment but IJSRuntime not provided. " +
                    "Either call CentrifugeClient.InitializeBrowserInterop(jsRuntime) at application startup, " +
                    "or pass IJSRuntime via CentrifugeClientOptions.JSRuntime.");
            }
#endif
        }

        /// <summary>
        /// Maps the scheme of an absolute endpoint URL to a transport type (ws/wss to WebSocket, http/https
        /// to HTTP streaming). Anything else throws, since no transport could connect to it.
        /// </summary>
        private static CentrifugeTransportType TransportOf(string? endpoint, string paramName) =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                ? uri.Scheme switch
                {
                    "ws" or "wss" => CentrifugeTransportType.WebSocket,
                    "http" or "https" => CentrifugeTransportType.HttpStream,
                    _ => throw new ArgumentException("Endpoint must start with ws://, wss://, http://, or https://", paramName)
                }
                : throw new ArgumentException($"Endpoint is not an absolute URL: '{endpoint}'", paramName);

        /// <summary>True when transports go through JS interop, which passes endpoints to fetch/WebSocket unchanged.</summary>
        private bool IsBrowser
        {
            get
            {
#if NET6_0_OR_GREATER
                return _jsRuntime != null;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// HTTP streaming posts commands to the emulation endpoint. If that is not an http/https URL every
        /// command would fail once connected, so it is rejected here instead of causing endless reconnects.
        /// A page-relative URL is accepted in the browser, where fetch resolves it.
        /// </summary>
        private void ValidateEmulationEndpoint()
        {
            var endpoint = _options.EmulationEndpoint;
            if (string.IsNullOrEmpty(endpoint) || IsHttpUrl(endpoint)) return;
            if (IsBrowser && Uri.TryCreate(endpoint, UriKind.Relative, out _)) return;

            throw new CentrifugeConfigurationException(IsBrowser
                ? "EmulationEndpoint must be an http:// or https:// URL or relative to the page"
                : "EmulationEndpoint must be an absolute http:// or https:// URL");
        }

        /// <summary>Checks for an absolute URL with an http or https scheme. UriKind.Absolute alone is not
        /// enough: on non-Windows systems a path like /x parses as an absolute file: URI.</summary>
        private static bool IsHttpUrl(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

        private bool IsDisposed => _disposed != 0;

        private void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CentrifugeClient));
        }

        internal TimeSpan Timeout => _options.Timeout;

        internal ILogger? Logger => _logger;

        private int TimeoutMs => (int)_options.Timeout.TotalMilliseconds;

        /// <summary>
        /// Connects to the server. Returns at once; the connection is established in the background. Use
        /// <see cref="ReadyAsync"/> or the <see cref="Connected"/> event to know when it is.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
        public void Connect()
        {
            ThrowIfDisposed();
            Loop.Invoke(() =>
            {
                if (IsDisposed || _state != CentrifugeClientState.Disconnected) return;
                _reconnectAttempts = 0;
                StartConnecting();
            });
        }

        /// <summary>
        /// Disconnects from the server. Subscriptions stay, subscribing: the next <see cref="Connect"/>
        /// subscribes them again, with recovery.
        /// </summary>
        public void Disconnect()
        {
            Loop.Invoke(() => DisconnectInternal(CentrifugeDisconnectedCodes.DisconnectCalled, "disconnect called", false));
        }

        /// <summary>
        /// Waits until the client is connected. Completes immediately if it already is; fails if the client
        /// is disconnected, or becomes disconnected before connecting.
        /// </summary>
        /// <param name="timeout">Optional timeout.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task completing on connect, or failing with <see cref="CentrifugeTimeoutException"/>
        /// once <paramref name="timeout"/> passes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The timeout is negative but not infinite, or exceeds what a timer supports.</exception>
        public Task ReadyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Utilities.ValidateWaitTimeout(timeout, nameof(timeout));
            if (IsDisposed) return Task.FromException(new ObjectDisposedException(nameof(CentrifugeClient)));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() =>
            {
                switch (_state)
                {
                    // Decided at once: completed at once, so a handler blocking on it (unsupported, but it worked
                    // before) doesn't wait for the step it holds.
                    case CentrifugeClientState.Disconnected:
                        ready.TrySetException(DisconnectedError());
                        return;
                    case CentrifugeClientState.Connected:
                        ready.TrySetResult(true);
                        return;
                    default:
                        _promises.Add(() => Loop.Complete(() => ready.TrySetResult(true)),
                            ex => Loop.Complete(() => Waiters.Fail(ready, ex)), TimeoutOf(timeout), cancellationToken);
                        return;
                }
            });
            return ready.Task;
        }

        private static int? TimeoutOf(TimeSpan? timeout) =>
            timeout is { } t && t != System.Threading.Timeout.InfiniteTimeSpan ? (int)t.TotalMilliseconds : null;

        /// <summary>
        /// Creates a new subscription to a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="options">Subscription options.</param>
        /// <returns>The subscription instance.</returns>
        /// <exception cref="CentrifugeDuplicateSubscriptionException">The client has a subscription of the channel.</exception>
        /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
        public CentrifugeSubscription NewSubscription(string channel, CentrifugeSubscriptionOptions? options = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentException("Channel cannot be null or empty", nameof(channel));
            }

            var subscription = new CentrifugeSubscription(this, channel, options);
            if (!_subscriptions.TryAdd(channel, subscription))
            {
                throw new CentrifugeDuplicateSubscriptionException(channel);
            }
            if (IsDisposed)
            {
                // A concurrent Dispose may have missed it.
                Unregister(subscription);
                ThrowIfDisposed();
            }
            return subscription;
        }

        /// <summary>
        /// Gets an existing subscription.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <returns>The subscription instance, or null if not found.</returns>
        public CentrifugeSubscription? GetSubscription(string channel)
        {
            _subscriptions.TryGetValue(channel, out var subscription);
            return subscription;
        }

        /// <summary>
        /// Unsubscribes and drops a subscription of this client (see <see cref="CentrifugeSubscription.Dispose"/>);
        /// a new subscription to the channel can be created right after. A subscription this client does not hold,
        /// e.g. one already removed or created by another client, is ignored, as in centrifuge-js.
        /// </summary>
        /// <param name="subscription">The subscription to remove.</param>
        public void RemoveSubscription(CentrifugeSubscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            if (ReferenceEquals(GetSubscription(subscription.Channel), subscription))
                subscription.Dispose();
        }

        /// <summary>Drops <paramref name="subscription"/> from the registry, unless another one has taken its channel.</summary>
        internal bool Unregister(CentrifugeSubscription subscription) =>
            ((ICollection<KeyValuePair<string, CentrifugeSubscription>>)_subscriptions)
                .Remove(new KeyValuePair<string, CentrifugeSubscription>(subscription.Channel, subscription));

        /// <summary>
        /// Gets all subscriptions.
        /// </summary>
        public IReadOnlyDictionary<string, CentrifugeSubscription> Subscriptions => _subscriptions;

        /// <summary>
        /// Sets the token for the next connect. If GetToken is not configured, this also clears the expired
        /// flag, so the next connect uses the new token rather than failing as unauthorized. With GetToken,
        /// an expired token is still refreshed through the callback, as in centrifuge-js.
        /// </summary>
        /// <param name="token">New connection token (JWT).</param>
        public void SetToken(string? token)
        {
            Loop.Invoke(() =>
            {
                _token = token ?? string.Empty;
                if (_options.GetToken == null) _refreshRequired = false;
            });
        }

        /// <summary>
        /// Sets the data sent with the next connect. A copy of the data is stored.
        /// </summary>
        /// <param name="data">New connection data.</param>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            var copy = data.IsEmpty ? default : data.ToArray();
            Loop.Invoke(() => _data = copy);
        }

        /// <summary>
        /// Sets the headers sent with the next connect command (header emulation, Centrifugo v6+). A copy of
        /// the dictionary is stored.
        /// </summary>
        /// <param name="headers">Headers to set; null values are not allowed.</param>
        public void SetHeaders(Dictionary<string, string>? headers)
        {
            if (headers != null && headers.ContainsValue(null!))
                throw new ArgumentException("Header values must not be null", nameof(headers));
            var copy = headers != null ? new Dictionary<string, string>(headers) : null;
            Loop.Invoke(() => _headers = copy);
        }

        /// <summary>
        /// Sends an RPC call to the server. If the client is not connected yet, the call first waits for the
        /// connection; each of the two waits (connection, reply) is limited by the client timeout.
        /// </summary>
        /// <param name="method">RPC method name.</param>
        /// <param name="data">Request data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>RPC result.</returns>
        public async Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var reply = await CallAsync(new Command
            {
                Rpc = new RPCRequest
                {
                    Method = method,
                    Data = ByteString.CopyFrom(data.Span)
                }
            }, cancellationToken).ConfigureAwait(false);

            return new CentrifugeRpcResult(reply.Rpc?.Data.ToByteArray() ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Sends an asynchronous message to the server; the server sends no reply. If the client is not
        /// connected yet, waits for the connection first, within the client timeout. The task completes when
        /// the message has been written to the transport, and fails on a write error or if the connection
        /// ends before the write.
        /// </summary>
        /// <param name="data">Message data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var command = new Command
            {
                Send = new SendRequest
                {
                    Data = ByteString.CopyFrom(data.Span)
                }
            };
            if (IsDisposed) return Task.FromException(new ObjectDisposedException(nameof(CentrifugeClient)));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() => MethodCall(() =>
            {
                if (_transport == null)
                {
                    Loop.Complete(() => sent.TrySetException(WriteError(null)));
                    return;
                }
                AddCommand(command, sent);
            }, ex => Loop.Complete(() => Waiters.Fail(sent, ex)), cancellationToken));
            return sent.Task;
        }

        /// <summary>
        /// Gets presence information for a channel. If the client is not connected yet, waits for the
        /// connection first, within the client timeout.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence result.</returns>
        public async Task<CentrifugePresenceResult> PresenceAsync(string channel, CancellationToken cancellationToken = default)
        {
            var reply = await CallAsync(new Command
            {
                Presence = new PresenceRequest { Channel = channel }
            }, cancellationToken).ConfigureAwait(false);

            var clients = new Dictionary<string, CentrifugeClientInfo>();
            foreach (var kvp in reply.Presence.Presence)
            {
                clients[kvp.Key] = CentrifugeClientInfo.FromProtocol(kvp.Value);
            }

            return new CentrifugePresenceResult(clients);
        }

        /// <summary>
        /// Gets presence stats for a channel. If the client is not connected yet, waits for the connection
        /// first, within the client timeout.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence stats result.</returns>
        public async Task<CentrifugePresenceStatsResult> PresenceStatsAsync(string channel, CancellationToken cancellationToken = default)
        {
            var reply = await CallAsync(new Command
            {
                PresenceStats = new PresenceStatsRequest { Channel = channel }
            }, cancellationToken).ConfigureAwait(false);

            return new CentrifugePresenceStatsResult(
                reply.PresenceStats.NumClients,
                reply.PresenceStats.NumUsers
            );
        }

        /// <summary>
        /// Publishes data to a channel; no client-side subscription to it is needed. If the client is not
        /// connected yet, waits for the connection first, within the client timeout.
        /// </summary>
        /// <param name="channel">Channel name to publish to.</param>
        /// <param name="data">Data to publish.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task PublishAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await CallAsync(new Command
            {
                Publish = new PublishRequest
                {
                    Channel = channel,
                    Data = ByteString.CopyFrom(data.Span)
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets channel history; no client-side subscription to it is needed. If the client is not connected
        /// yet, waits for the connection first, within the client timeout. Without a limit > 0 only the current
        /// stream position is returned, with no publications.
        /// </summary>
        /// <param name="channel">Channel name to get history for.</param>
        /// <param name="options">History options (limit, since position, reverse order).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>History result with publications and stream position.</returns>
        public async Task<CentrifugeHistoryResult> HistoryAsync(string channel, CentrifugeHistoryOptions? options = null, CancellationToken cancellationToken = default)
        {
            var reply = await CallAsync(new Command
            {
                History = HistoryRequestOf(channel, options)
            }, cancellationToken).ConfigureAwait(false);

            return HistoryResultOf(channel, reply.History);
        }

        internal static HistoryRequest HistoryRequestOf(string channel, CentrifugeHistoryOptions? options)
        {
            var request = new HistoryRequest
            {
                Channel = channel
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
            return request;
        }

        internal static CentrifugeHistoryResult HistoryResultOf(string channel, HistoryResult result)
        {
            var publications = new List<CentrifugePublicationEventArgs>();
            foreach (var pub in result.Publications)
            {
                publications.Add(CreatePublicationArgs(channel, pub));
            }

            return new CentrifugeHistoryResult(publications.ToArray(), result.Epoch, result.Offset);
        }

        /// <summary>
        /// A method call (RPC, publish, history, presence…): waits for the client to be connected (up to the
        /// timeout, as centrifuge-js _methodCall), then sends the command and waits for its reply (up to the
        /// timeout). An error reply fails the task with it.
        /// </summary>
        internal Task<Reply> CallAsync(Command command, CancellationToken cancellationToken)
        {
            if (IsDisposed) return Task.FromException<Reply>(new ObjectDisposedException(nameof(CentrifugeClient)));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<Reply>(cancellationToken);
            var result = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() => MethodCall(() => SendCall(command, result, cancellationToken),
                ex => Loop.Complete(() => Waiters.Fail(result, ex)), cancellationToken));
            return result.Task;
        }

        /// <summary>Proceeds at once when connected, else when the client connects within the timeout; fails
        /// when it disconnects first (as centrifuge-js _methodCall). Call in a step.</summary>
        private void MethodCall(Action proceed, Action<Exception> fail, CancellationToken cancellationToken)
        {
            if (_state == CentrifugeClientState.Connected)
            {
                proceed();
                return;
            }
            _promises.Add(proceed, fail, TimeoutMs, cancellationToken);
        }

        /// <summary>Registers the call of <paramref name="command"/> and queues it. Call in a step.</summary>
        private void SendCall(Command command, TaskCompletionSource<Reply> result, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Loop.Complete(() => result.TrySetCanceled(cancellationToken));
                return;
            }
            var id = RegisterCall(command, reply => Loop.Complete(() => result.TrySetResult(reply)),
                (error, _) => Loop.Complete(() => Waiters.Fail(result, error)));
            AddCommand(command);
            if (!cancellationToken.CanBeCanceled) return;
            var registration = cancellationToken.Register(() => Loop.Execute(() =>
            {
                if (!_calls.TryGetValue(id, out var call)) return;
                _calls.Remove(id);
                call.Timer?.Cancel();
                Loop.Complete(() => result.TrySetCanceled(cancellationToken));
            }));
            result.Task.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        }

        // ---------------------------------------------------------------------------------------------
        // Connection lifecycle (centrifuge-js _startConnecting, _startReconnecting, _initializeTransport…)

        /// <summary>
        /// Changes the state, raising StateChanged. Returns the number of the transition made, or 0 if the
        /// client already was in <paramref name="newState"/>.
        /// </summary>
        private int SetState(CentrifugeClientState newState)
        {
            if (_state == newState) return 0;
            _reconnecting = false;
            var oldState = _state;
            _state = newState;
            var transition = ++_stateTransitions;
            Raise(StateChanged, new CentrifugeStateEventArgs(oldState, newState), "stateChanged");
            return transition;
        }

        /// <summary>Whether an event handler made another state transition after <paramref name="transition"/>
        /// (as returned by SetState), so that it is outdated.</summary>
        private bool TransitionSuperseded(int transition) => transition != 0 && _stateTransitions != transition;

        private void StartConnecting()
        {
            var transition = _state != CentrifugeClientState.Connecting ? _stateTransitions + 1 : 0;
            SetState(CentrifugeClientState.Connecting);
            // A StateChanged handler may have called Disconnect() already.
            if (transition != 0 && !TransitionSuperseded(transition))
            {
                Raise(Connecting, new CentrifugeConnectingEventArgs(CentrifugeConnectingCodes.ConnectCalled, "connect called"), "connecting");
            }
            if (!TransitionSuperseded(transition))
            {
                _clientId = null;
                StartReconnecting();
            }
        }

        private void StartReconnecting()
        {
            if (_state != CentrifugeClientState.Connecting) return;
            if (_reconnecting) return;
            if (!_transportClosed) return;

            _reconnecting = true;
            // A disconnect while the token is loading aborts this attempt: a new one is scheduled by the
            // disconnect, so the continuation must not act on it.
            var disconnects = _disconnects;
            var needTokenRefresh = _refreshRequired || (_token.Length == 0 && _options.GetToken != null);
            if (!needTokenRefresh)
            {
                InitializeTransport();
                return;
            }

            GetConnectionToken(token =>
            {
                if (AttemptAborted(disconnects)) return;
                if (token == null)
                {
                    FailUnauthorized();
                    return;
                }
                _token = token;
                InitializeTransport();
            }, error =>
            {
                if (AttemptAborted(disconnects)) return;
                if (error is CentrifugeUnauthorizedException)
                {
                    FailUnauthorized();
                    return;
                }
                EmitError("connectToken", new CentrifugeException(CentrifugeErrorCodes.ClientConnectToken, error.Message, true, error), () =>
                {
                    var delay = GetReconnectDelay();
                    _reconnecting = false;
                    ClearReconnectTimer();
                    _reconnectTimer = LoopTimer.Start(Loop, delay, () =>
                    {
                        _reconnectTimer = null;
                        StartReconnecting();
                    });
                });
            });
        }

        /// <summary>
        /// Gets a connection token from GetToken, off the loop: <paramref name="onToken"/> or
        /// <paramref name="onError"/> runs in a later step. Without GetToken: a configuration error, then
        /// unauthorized (as centrifuge-js).
        /// </summary>
        private void GetConnectionToken(Action<string?> onToken, Action<Exception> onError)
        {
            var getToken = _options.GetToken;
            if (getToken == null)
            {
                EmitErrorEvent("configuration", new CentrifugeConfigurationException("token expired but no GetToken function set in the configuration"));
                Loop.Defer(() => onError(new CentrifugeUnauthorizedException()));
                return;
            }
            Callbacks.Run(Loop, () => getToken(), onToken, onError);
        }

        /// <summary>Emits an error event, then runs the continuation unless a handler disconnected the client
        /// (maybe to connect again: the continuation must not touch that attempt).</summary>
        private void EmitError(string type, Exception error, Action continuation)
        {
            var disconnects = _disconnects;
            EmitErrorEvent(type, error);
            if (_disconnects == disconnects) continuation();
        }

        private bool AttemptAborted(int disconnects) =>
            _state != CentrifugeClientState.Connecting || _disconnects != disconnects;

        private int GetReconnectDelay()
        {
            var delay = Utilities.CalculateBackoff(_reconnectAttempts, _options.MinReconnectDelay, _options.MaxReconnectDelay);
            _reconnectAttempts++;
            return delay;
        }

        /// <summary>An emulation connection resets the backoff once it proved usable (see ConnectResponse).</summary>
        private void ResetReconnectAttemptsIfPending()
        {
            if (!_reconnectAttemptsResetPending) return;
            _reconnectAttemptsResetPending = false;
            _reconnectAttempts = 0;
        }

        private void AdvanceTransportIndex()
        {
            if (_transportEndpoints == null) return;
            _currentTransportIndex++;
            if (_currentTransportIndex >= _transportEndpoints.Count)
            {
                _triedAllTransports = true;
                _currentTransportIndex = 0;
            }
        }

        /// <summary>A failure of a transport that never proved to reach the server moves fallback mode on to the next endpoint.</summary>
        private void AdvanceUnprovenTransport()
        {
            if (_transportEndpoints != null && !_transportWasOpen) AdvanceTransportIndex();
        }

        /// <summary>Per-attempt state the transport callbacks share.</summary>
        private sealed class Attempt
        {
            public Attempt(int id, ITransport transport)
            {
                Id = id;
                Transport = transport;
            }

            public int Id { get; }

            public ITransport Transport { get; }

            /// <summary>The transport opened.</summary>
            public bool WasOpen { get; set; }
        }

        private void InitializeTransport()
        {
            CentrifugeTransportType type;
            string endpoint;
            if (_transportEndpoints != null)
            {
                if (_currentTransportIndex >= _transportEndpoints.Count)
                {
                    _triedAllTransports = true;
                    _currentTransportIndex = 0;
                }
                (type, endpoint) = (_transportEndpoints[_currentTransportIndex].Transport, _transportEndpoints[_currentTransportIndex].Endpoint);
            }
            else
            {
                (type, endpoint) = (_endpointTransport, _endpoint!);
            }

            ITransport transport;
            try
            {
                transport = CreateTransport(type, endpoint);
            }
            catch (Exception ex)
            {
                // E.g. an endpoint the transport can't parse: a failure of the attempt, retried — with the
                // next endpoint in fallback mode (as centrifuge-js failTransport).
                AdvanceUnprovenTransport();
                DisconnectInternal(CentrifugeConnectingCodes.TransportClosed, "transport closed", true);
                EmitErrorEvent("transport", new CentrifugeException(CentrifugeErrorCodes.TransportClosed, ex.Message, true, ex));
                return;
            }

            var id = ++_transportId;
            var attempt = new Attempt(id, transport);
            _transport = transport;
            _writer = new TransportWriter(transport, (frame, error) => Loop.Execute(() => OnFrameWritten(attempt, frame, error)), _options.Timeout);
            _emulationEndpoint = transport.UsesEmulation ? EmulationEndpointFor(endpoint) : null;

            byte[]? initialData = null;
            if (transport.UsesEmulation)
            {
                // The connect command goes with the request that opens the stream.
                initialData = SendConnect(skipSending: true).ToByteArray();
            }

            _transportClosed = false;

            // Fails an attempt that didn't open in time. It doesn't wait for the transport's close.
            ClearConnectTimer();
            // The open (WebSocket handshake, HTTP stream response) is bounded by Timeout, as the connect
            // timeout of centrifuge-js.
            var openTimeoutMs = TimeoutMs;
            var due = Clock.NowMs + openTimeoutMs;
            _connectTimer = LoopTimer.Start(Loop, openTimeoutMs, () =>
            {
                // The transport may have opened already, its open not handled yet (a suspended process
                // resumed): let it be handled first.
                var late = Clock.NowMs - due >= LateTimerMs;
                _connectTimer = LoopTimer.Start(Loop, late ? LateTimerGraceMs : 0, () =>
                {
                    _connectTimer = null;
                    FailTransport(attempt, "connect timeout");
                });
            });

            transport.Opened += (_, _) => Loop.Execute(() => OnTransportOpen(attempt));
            transport.MessageReceived += (_, messages) =>
            {
                // The transport reuses the list once this returns.
                var frame = messages.ToArray();
                Loop.Execute(() => OnTransportFrame(attempt, frame));
            };
            transport.Closed += (_, e) => Loop.Execute(() => OnTransportClose(attempt, e.Code, e.Reason));
            transport.Error += (_, e) => Loop.Execute(() =>
            {
                if (attempt.Id == _transportId) EmitErrorEvent("transport", e);
            });

            var openCts = new CancellationTokenSource();
            _openCts = openCts;
            _ = OpenTransportAsync(attempt, initialData, openCts.Token);
        }

        private async Task OpenTransportAsync(Attempt attempt, byte[]? initialData, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() => attempt.Transport.OpenAsync(cancellationToken, initialData)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A transport that fails to open: as one closing before it opened.
                _logger?.LogDebug(ex, "Transport failed to open");
                Loop.Execute(() => OnTransportClose(attempt, null, string.Empty, ex));
            }
        }

        private ITransport CreateTransport(CentrifugeTransportType transportType, string endpoint)
        {
            switch (transportType)
            {
                case CentrifugeTransportType.WebSocket:
#if NET6_0_OR_GREATER
                    if (_jsRuntime != null) return new BrowserWebSocketTransport(endpoint, _jsRuntime, _logger);
#endif
                    return new WebSocketTransport(endpoint);
                case CentrifugeTransportType.HttpStream:
#if NET6_0_OR_GREATER
                    if (_jsRuntime != null) return new BrowserHttpStreamTransport(endpoint, _jsRuntime, _logger);
#endif
                    return new HttpStreamTransport(endpoint);
                default:
                    throw new CentrifugeConfigurationException($"Unsupported transport type: {transportType}");
            }
        }

        /// <summary>Emulation endpoint to use with a transport for <paramref name="endpoint"/>. Uses the
        /// configured value if set, else /emulation at the root of the endpoint's host (the page's host
        /// when a browser endpoint is relative).</summary>
        private string EmulationEndpointFor(string endpoint)
        {
            if (!string.IsNullOrEmpty(_options.EmulationEndpoint))
            {
                return _options.EmulationEndpoint!;
            }

            return IsHttpUrl(endpoint)
                ? $"{new Uri(endpoint).GetLeftPart(UriPartial.Authority)}/emulation"
                : "/emulation";
        }

        /// <summary>Fails an attempt that didn't open in time (as centrifuge-js failTransport).</summary>
        private void FailTransport(Attempt attempt, string reason)
        {
            if (attempt.Id != _transportId) return;
            AdvanceUnprovenTransport();
            // The teardown fails the connect command pending on it: the transport error reports it.
            _abortedTransportId = attempt.Id;
            DisconnectInternal(CentrifugeConnectingCodes.TransportClosed, "transport closed", true);
            EmitErrorEvent("transport", new CentrifugeException(CentrifugeErrorCodes.TransportClosed, reason, true));
        }

        private void OnTransportOpen(Attempt attempt)
        {
            if (attempt.Id != _transportId)
            {
                _ = CloseTransportAsync(attempt.Transport);
                return;
            }
            ClearConnectTimer();
            attempt.WasOpen = true;
            if (attempt.Transport.UsesEmulation) return;
            _transportIsOpen = true;
            // A socket that opened keeps its transport: a slow first connect reply must not move the client to
            // a fallback transport for good.
            _transportWasOpen = true;
            // The connect and the subscribe commands go in one frame (written when the step ends).
            SendConnect(skipSending: false);
            SendSubscribeCommands();
        }

        /// <summary>The transport closed (or failed to open: <paramref name="cause"/>, reported with the error).</summary>
        /// <param name="attempt">The attempt of the transport.</param>
        /// <param name="closeCode">The close code, if any.</param>
        /// <param name="closeReason">The close reason, if any.</param>
        /// <param name="cause">Why the transport failed to open, for the error reported.</param>
        private void OnTransportClose(Attempt attempt, int? closeCode, string closeReason, Exception? cause = null)
        {
            if (attempt.Id != _transportId) return;
            ClearConnectTimer();
            _transportClosed = true;
            _transportIsOpen = false;

            var code = closeCode ?? 0;
            var reason = string.IsNullOrEmpty(closeReason) ? "connection closed" : closeReason;
            var reconnect = !IsPermanentDisconnectCode(code);
            if (code < 3000)
            {
                if (code == 1009)
                {
                    code = CentrifugeDisconnectedCodes.MessageSizeLimit;
                    reason = "message size limit exceeded";
                    reconnect = false;
                }
                else
                {
                    code = CentrifugeConnectingCodes.TransportClosed;
                    reason = "transport closed";
                }
                AdvanceUnprovenTransport();
            }
            else
            {
                // A code from the server application: the transport reached it.
                _transportWasOpen = true;
            }

            void Teardown()
            {
                _reconnecting = false;
                DisconnectInternal(code, reason, reconnect);
            }

            if (_state == CentrifugeClientState.Connecting && !attempt.WasOpen)
            {
                // The teardown fails the connect command pending on it (emulation): the transport error
                // reports the failure, not a connect error too.
                _abortedTransportId = attempt.Id;
                var error = cause == null
                    ? new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "transport closed", true)
                    : new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "transport closed: " + (cause.InnerException ?? cause).Message, true, cause);
                EmitError("transport", error, Teardown);
            }
            else
            {
                Teardown();
            }
        }

        /// <summary>Disconnect codes after which the client does not reconnect: 3500-3999 and 4500-4999.</summary>
        private static bool IsPermanentDisconnectCode(int code) =>
            (code >= 3500 && code < 4000) || (code >= 4500 && code < 5000);

        /// <summary>
        /// A frame: its messages are dispatched one by one, in order. One that isn't Centrifugo data (e.g. an
        /// HTML page of a captive portal) tears the connection down at once: nothing after it is dispatched,
        /// and recovery resumes after the messages before it.
        /// </summary>
        private void OnTransportFrame(Attempt attempt, byte[][] messages)
        {
            if (attempt.Id != _transportId) return;
            // Only data from the server proves the connection alive.
            if (_serverPing > 0 && messages.Length > 0) WaitServerPing();

            foreach (var message in messages)
            {
                // The transport was closed or replaced while dispatching (a handler disconnected): the rest
                // of the frame is outdated.
                if (attempt.Id != _transportId) return;
                Reply reply;
                try
                {
                    reply = Reply.Parser.ParseFrom(message);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error decoding received data");
                    EmitErrorEvent("parse", ex);
                    if (attempt.Id != _transportId) return;
                    AdvanceUnprovenTransport();
                    DisconnectInternal(CentrifugeConnectingCodes.TransportClosed, "transport closed", true);
                    return;
                }
                DispatchReply(reply);
            }
        }

        private void DispatchReply(Reply reply)
        {
            if (reply.Id > 0)
            {
                HandleReply(reply);
            }
            else if (reply.Push == null)
            {
                HandleServerPing();
            }
            else
            {
                HandlePush(reply.Push);
            }
        }

        /// <summary>
        /// Builds the connect command and registers its call: its reply connects (ConnectResponse), its
        /// error fails the attempt (ConnectError). Over emulation it goes with the open request
        /// (<paramref name="skipSending"/>), otherwise it is queued.
        /// </summary>
        private Command SendConnect(bool skipSending)
        {
            var command = BuildConnectCommand();
            // A teardown fails the pending command, which must not tear down a newer attempt.
            var transportId = _transportId;
            var token = _token;
            RegisterCall(command, reply =>
            {
                if (reply.Connect != null)
                {
                    ConnectResponse(reply.Connect);
                    return;
                }
                // A reply breaking the protocol: the attempt fails as with a temporary error.
                if (_state != CentrifugeClientState.Connecting) return;
                EmitError("connect", new InvalidDataException("connect reply without result"), () =>
                {
                    _transportWasOpen = true;
                    DisconnectInternal(CentrifugeConnectingCodes.TransportClosed, "transport closed", true);
                });
            }, (error, replied) =>
            {
                if (transportId == _abortedTransportId) return;
                ConnectError(error, _transportId != transportId, replied, token);
            });
            if (!skipSending) AddCommand(command, urgent: true);
            return command;
        }

        private Command BuildConnectCommand()
        {
            var request = new ConnectRequest
            {
                Token = _token,
                Name = _options.Name,
                Version = _options.Version
            };
            if (!_data.IsEmpty) request.Data = ByteString.CopyFrom(_data.Span);
            if (_headers != null)
            {
                foreach (var kvp in _headers) request.Headers.Add(kvp.Key, kvp.Value);
            }

            foreach (var kvp in _serverSubs)
            {
                if (!kvp.Value.Recoverable) continue;
                // A publication being dispatched counts as delivered: a connect built from its handler
                // recovers after it.
                var offset = _pendingServerSubOffsets.TryGetValue(kvp.Key, out var pending) ? pending : kvp.Value.Offset;
                request.Subs.Add(kvp.Key, new SubscribeRequest
                {
                    Channel = kvp.Key,
                    Recover = true,
                    Offset = offset,
                    Epoch = kvp.Value.Epoch
                });
            }

            return new Command { Connect = request };
        }

        /// <param name="error">The error.</param>
        /// <param name="stale">The error belongs to a transport already closed: reported, but the current
        /// attempt is not torn down.</param>
        /// <param name="replied">The server returned the error, so the transport works.</param>
        /// <param name="token">The token the connect command carried.</param>
        private void ConnectError(CentrifugeException error, bool stale, bool replied, string token)
        {
            if (_state != CentrifugeClientState.Connecting) return;
            // Token expired: the next attempt gets a new one, unless the app set one meanwhile (see SetToken).
            if (error.Code == 109 && token == _token) _refreshRequired = true;
            if (error.Code < 100 || error.Temporary || error.Code == 109)
            {
                EmitError("connect", error, () =>
                {
                    if (stale) return;
                    if (replied)
                    {
                        // The transport reached the server: retry it after the reconnect delay.
                        _transportWasOpen = true;
                    }
                    if (!replied) AdvanceUnprovenTransport();
                    DisconnectInternal(error.Code, error.Message, true);
                });
            }
            else if (!stale)
            {
                DisconnectInternal(error.Code, error.Message, false);
            }
        }

        private void ScheduleReconnect()
        {
            if (_state != CentrifugeClientState.Connecting) return;
            // The first round over the fallback transports doesn't count towards the backoff.
            var initialHandshake = _transportEndpoints != null && !_transportWasOpen && !_triedAllTransports;
            var delay = initialHandshake ? 0 : GetReconnectDelay();
            ClearReconnectTimer();
            _reconnectTimer = LoopTimer.Start(Loop, delay, () =>
            {
                _reconnectTimer = null;
                StartReconnecting();
            });
        }

        private void ConnectResponse(ConnectResult result)
        {
            _transportIsOpen = true;
            _transportWasOpen = true;
            if (_transport != null && _transport.UsesEmulation)
            {
                // A connect reply over emulation proves only the stream: the backoff resets once the server
                // replied to a command or pinged, or a client whose commands keep failing reconnects at the
                // minimum delay forever.
                _reconnectAttemptsResetPending = true;
            }
            else
            {
                _reconnectAttempts = 0;
            }
            _refreshRequired = false;

            if (_state == CentrifugeClientState.Connected) return;

            _clientId = result.Client;
            _connection++;
            // Before the events: a handler's commands go to the emulation session of this connection.
            _session = result.Session;
            _node = result.Node;
            var transition = SetState(CentrifugeClientState.Connected);
            // A StateChanged handler may have disconnected already.
            if (TransitionSuperseded(transition)) return;

            ClearRefreshTimer();
            if (result.Expires) ScheduleRefresh(Utilities.TtlToMilliseconds(result.Ttl));

            SendSubscribeCommands();

            Raise(Connected, new CentrifugeConnectedEventArgs(result.Client, _transport?.Name ?? string.Empty, result.Data.ToByteArray()), "connected");
            if (TransitionSuperseded(transition)) return;

            _promises.ResolveAll();

            ProcessServerSubs(result.Subs, transition);

            if (result.Ping > 0)
            {
                _serverPing = result.Ping;
                _sendPong = result.Pong;
                WaitServerPing();
            }
            else
            {
                _serverPing = 0;
            }
        }

        /// <summary>
        /// The server-side subscriptions of a connect reply: each subscribed, then the recovered publications,
        /// then those the reply doesn't have any longer unsubscribed. A handler may disconnect: the rest of this
        /// connection's subscribed and publication events aren't raised, and no position is stored for them.
        /// </summary>
        private void ProcessServerSubs(Google.Protobuf.Collections.MapField<string, SubscribeResult> subs, int transition)
        {
            foreach (var kvp in subs)
            {
                if (TransitionSuperseded(transition)) break;
                var sub = kvp.Value;
                _serverSubs[kvp.Key] = new ServerSubscription(sub.Offset, sub.Epoch, sub.Recoverable) { Subscribed = true };
                Raise(ServerSubscribed, ServerSubscribedArgs(kvp.Key, sub.WasRecovering, sub.Recovered, sub.Recoverable,
                    sub.Positioned, sub.Offset, sub.Epoch, sub.Data), "serverSubscribed");
            }

            foreach (var kvp in subs)
            {
                if (!kvp.Value.Recovered) continue;
                foreach (var pub in kvp.Value.Publications)
                {
                    if (TransitionSuperseded(transition)) break;
                    HandleServerPublication(kvp.Key, pub);
                }
            }

            foreach (var channel in _serverSubs.Keys.ToList())
            {
                if (subs.ContainsKey(channel)) continue;
                _serverSubs.Remove(channel);
                Raise(ServerUnsubscribed, new CentrifugeServerUnsubscribedEventArgs(channel), "serverUnsubscribed");
            }
        }

        private static CentrifugeServerSubscribedEventArgs ServerSubscribedArgs(string channel, bool wasRecovering, bool recovered,
            bool recoverable, bool positioned, ulong offset, string epoch, ByteString data) =>
            new CentrifugeServerSubscribedEventArgs(channel, wasRecovering, recovered, recoverable, positioned,
                positioned || recoverable ? new CentrifugeStreamPosition(offset, epoch) : (CentrifugeStreamPosition?)null,
                data.ToByteArray());

        /// <summary>
        /// Tears the connection down: to Connecting with <paramref name="reconnect"/>, else to Disconnected
        /// (as centrifuge-js _disconnect). Everything of the transport is dropped before any event — handlers
        /// may connect or disconnect — then StateChanged, the subscriptions' Subscribing and the client's
        /// Connecting or Disconnected, unless a handler made another transition meanwhile.
        /// </summary>
        internal void DisconnectInternal(int code, string reason, bool reconnect)
        {
            if (code == CentrifugeDisconnectedCodes.StateInvalidated)
            {
                // State invalidated (a Disconnect push or a close code): the next connect gets a fresh token when
                // GetToken can, and every position is made unrecoverable — before the subscriptions move to
                // subscribing below.
                if (_options.GetToken != null)
                {
                    _token = string.Empty;
                    _refreshRequired = true;
                }
                foreach (var sub in _subscriptions.Values) sub.InvalidateState();
                foreach (var serverSub in _serverSubs.Values)
                {
                    // "_" is a sentinel the server never produces: the recovery of the next connect fails (an
                    // empty epoch would mean any epoch to the server).
                    serverSub.Offset = 0;
                    serverSub.Epoch = "_";
                }
            }
            if (_state == CentrifugeClientState.Disconnected)
            {
                // Calls made while disconnected wait for a connection: Disconnect() ends that wait.
                if (!reconnect) _promises.RejectAll(DisconnectedError());
                return;
            }
            var previousState = _state;
            var newState = reconnect ? CentrifugeClientState.Connecting : CentrifugeClientState.Disconnected;

            _transportIsOpen = false;
            _reconnecting = false;
            if (previousState == CentrifugeClientState.Connecting) ClearReconnectTimer();
            ClearConnectTimer();
            // Invalidates the callbacks of the transport closed below.
            _transportId++;
            // Aborts token continuations of an attempt that had no transport yet.
            _disconnects++;
            if (_transport != null)
            {
                CloseCurrentTransport();
                // Its close is ignored, so a new attempt doesn't wait for it.
                _transportClosed = true;
            }
            // A connection that ends before it proved usable keeps the backoff growing.
            _reconnectAttemptsResetPending = false;
            // Commands queued for the closed transport don't go out on the next one.
            DropCommands();
            ClearOutgoingRequests();
            if (!reconnect) _promises.RejectAll(DisconnectedError());

            var transition = _state != newState ? _stateTransitions + 1 : 0;
            SetState(newState);
            if (!reconnect && !TransitionSuperseded(transition))
            {
                // Calls made from a StateChanged handler fail now too.
                _promises.RejectAll(DisconnectedError());
            }
            if (previousState == CentrifugeClientState.Connected)
            {
                ClearConnectedState();
            }
            else
            {
                // A subscription subscribed before the connect reply (its subscribe went with the connect) ends
                // with the transport too (TLA Teardown moves every subscribed subscription to subscribing).
                foreach (var sub in _subscriptions.Values)
                {
                    if (sub.State == CentrifugeSubscriptionState.Subscribed)
                        sub.MoveToSubscribing(CentrifugeSubscribingCodes.TransportClosed, "transport closed");
                }
            }
            // A handler of the events above may have changed the state again: this transition is outdated
            // then, and its event would arrive out of order.
            if (transition != 0 && !TransitionSuperseded(transition))
            {
                if (reconnect)
                    Raise(Connecting, new CentrifugeConnectingEventArgs(code, reason), "connecting");
                else
                    Raise(Disconnected, new CentrifugeDisconnectedEventArgs(code, reason), "disconnected");
            }
            if (reconnect && !TransitionSuperseded(transition)) ScheduleReconnect();
        }

        private void ClearConnectedState()
        {
            _clientId = null;
            _connection++;
            ClearServerPingTimer();
            ClearRefreshTimer();

            foreach (var sub in _subscriptions.Values)
            {
                if (sub.State == CentrifugeSubscriptionState.Subscribed)
                    sub.MoveToSubscribing(CentrifugeSubscribingCodes.TransportClosed, "transport closed");
            }

            foreach (var kvp in _serverSubs)
            {
                if (!kvp.Value.Subscribed) continue;
                kvp.Value.Subscribed = false;
                Raise(ServerSubscribing, new CentrifugeServerSubscribingEventArgs(kvp.Key), "serverSubscribing");
            }
        }

        private void FailUnauthorized() =>
            DisconnectInternal(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false);

        /// <summary>Closes the current transport: what its writer didn't write yet is dropped, the close and
        /// the disposal run in the background.</summary>
        private void CloseCurrentTransport()
        {
            var transport = _transport!;
            _transport = null;
            _emulationEndpoint = null;
            if (_writer != null)
            {
                foreach (var frame in _writer.Close()) FailSends(frame.Sends, ConnectionClosedError());
                _writer = null;
            }
            _openCts?.Cancel();
            _openCts?.Dispose();
            _openCts = null;
            _lastTransportClose = CloseTransportAsync(transport);
        }

        private static async Task CloseTransportAsync(ITransport transport)
        {
            try
            {
                await Task.Run(transport.CloseAsync).ConfigureAwait(false);
            }
            catch
            {
                // The connection is torn down anyway.
            }
            transport.Dispose();
        }

        // ---------------------------------------------------------------------------------------------
        // Commands and calls

        private sealed class PendingCall
        {
            public PendingCall(Action<Reply> onReply, Action<CentrifugeException, bool> onError)
            {
                OnReply = onReply;
                OnError = onError;
            }

            public Action<Reply> OnReply { get; }

            /// <summary>The error, and whether it came in a reply.</summary>
            public Action<CentrifugeException, bool> OnError { get; }

            public LoopTimer? Timer { get; set; }
        }

        /// <summary>
        /// Registers a call: <paramref name="onReply"/> gets its reply, <paramref name="onError"/> an error reply,
        /// a timeout, a write error or the end of the connection. Assigns the command id. Call in a step.
        /// </summary>
        internal uint RegisterCall(Command command, Action<Reply> onReply, Action<CentrifugeException, bool> onError)
        {
            var id = NextCommandId();
            command.Id = id;
            var call = new PendingCall(onReply, onError);
            _calls[id] = call;
            var due = Clock.NowMs + TimeoutMs;
            call.Timer = LoopTimer.Start(Loop, TimeoutMs, () =>
            {
                // The reply may have been received already, not processed yet: let it be processed first. A
                // timer running far too late means the process was suspended: the reply gets a grace period.
                var late = Clock.NowMs - due >= LateTimerMs;
                call.Timer = LoopTimer.Start(Loop, late ? LateTimerGraceMs : 0, () =>
                {
                    if (!_calls.Remove(id)) return;
                    onError(new CentrifugeTimeoutException("timeout"), false);
                });
            });
            return id;
        }

        private uint NextCommandId()
        {
            // Id 0 is reserved for pushes: skipped on wrap-around.
            unchecked
            {
                var id = (uint)++_commandId;
                return id != 0 ? id : (uint)++_commandId;
            }
        }

        /// <summary>
        /// Queues a command. The commands queued within <see cref="CommandBatchDelayMs"/> go out together — at
        /// once when the step ends with <paramref name="urgent"/> or a batch of MaxCommandBatchSize.
        /// <paramref name="sent"/>, for a command without reply, completes once it is written.
        /// </summary>
        internal void AddCommand(Command command, TaskCompletionSource<bool>? sent = null, bool urgent = false)
        {
            _commands.Add((command, sent));
            _commandsSize += command.CalculateSize() + 5;
            if (urgent) _flushNow = true;
        }

        private void OnStepEnd()
        {
            if (_commands.Count == 0) return;
            if (_flushNow || _commandsSize >= MaxCommandBatchSize)
            {
                WriteCommands();
                return;
            }
            _flushTimer ??= LoopTimer.Start(Loop, CommandBatchDelayMs, () =>
            {
                _flushTimer = null;
                WriteCommands();
            });
        }

        /// <summary>Writes the queued commands, in frames of up to MaxCommandBatchSize.</summary>
        private void WriteCommands()
        {
            _flushTimer?.Cancel();
            _flushTimer = null;
            _flushNow = false;
            _commandsSize = 0;
            if (_commands.Count == 0) return;
            var commands = _commands;
            _commands = new List<(Command, TaskCompletionSource<bool>?)>();
            if (_writer == null)
            {
                // No transport: the calls time out (as centrifuge-js), the sends fail now.
                FailSends(commands.Where(entry => entry.Sent != null).Select(entry => entry.Sent!).ToList(), WriteError(null));
                return;
            }

            var index = 0;
            while (index < commands.Count)
            {
                using var ms = new MemoryStream();
                List<uint>? callIds = null;
                List<TaskCompletionSource<bool>>? sends = null;
                do
                {
                    var entry = commands[index++];
                    VarintCodec.WriteDelimitedMessage(ms, entry.Command.ToByteArray());
                    if (entry.Command.Id > 0) (callIds ??= new List<uint>()).Add(entry.Command.Id);
                    if (entry.Sent != null) (sends ??= new List<TaskCompletionSource<bool>>()).Add(entry.Sent);
                } while (index < commands.Count && ms.Length < MaxCommandBatchSize);

                var frame = new OutgoingFrame(ms.ToArray(), callIds, sends);
                if (_emulationEndpoint != null)
                {
                    frame.Session = _session;
                    frame.Node = _node;
                    frame.EmulationEndpoint = _emulationEndpoint;
                }
                if (!_writer.Enqueue(frame)) FailSends(sends, ConnectionClosedError());
            }
        }

        private void DropCommands()
        {
            FailSends(_commands.Where(entry => entry.Sent != null).Select(entry => entry.Sent!).ToList(), ConnectionClosedError());
            _commands.Clear();
            _commandsSize = 0;
            _flushNow = false;
            _flushTimer?.Cancel();
            _flushTimer = null;
        }

        /// <summary>
        /// The outcome of a write. A failed one fails its calls with a write error (as centrifuge-js
        /// _handleWriteError). A WebSocket closes on a failed write and reports it; an emulation request that
        /// didn't reach a node of the session (no response, 404, 5xx) closes the transport, as centrifuge-js —
        /// other statuses, e.g. a too large request, fail only its commands.
        /// </summary>
        private void OnFrameWritten(Attempt attempt, OutgoingFrame frame, Exception? error)
        {
            if (error == null)
            {
                if (frame.Sends != null) Loop.Complete(() => { foreach (var sent in frame.Sends) sent.TrySetResult(true); });
                return;
            }
            _logger?.LogDebug(error, "Error writing commands");
            FailSends(frame.Sends, WriteError(error));
            if (attempt.Id != _transportId) return;
            if (frame.CallIds != null)
            {
                foreach (var id in frame.CallIds)
                {
                    if (!_calls.TryGetValue(id, out var call)) continue;
                    _calls.Remove(id);
                    call.Timer?.Cancel();
                    call.OnError(WriteError(error), false);
                }
            }
            if (attempt.Transport.UsesEmulation && EmulationTransportFailed(error))
            {
                OnTransportClose(attempt, null, string.Empty);
            }
        }

        internal static bool EmulationTransportFailed(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is EmulationStatusException status) return status.StatusCode == 404 || status.StatusCode >= 500;
            }
            return true;
        }

        private void FailSends(List<TaskCompletionSource<bool>>? sends, Exception error)
        {
            if (sends == null) return;
            Loop.Complete(() => { foreach (var sent in sends) sent.TrySetException(error); });
        }

        private void HandleReply(Reply reply)
        {
            ResetReconnectAttemptsIfPending();
            if (!_calls.TryGetValue(reply.Id, out var call)) return;
            _calls.Remove(reply.Id);
            call.Timer?.Cancel();
            if (reply.Error != null)
            {
                call.OnError(CentrifugeException.FromReply(reply.Error), true);
            }
            else
            {
                call.OnReply(reply);
            }
        }

        /// <summary>Fails the calls of the connection that ends: in a deferred step, after the teardown and its
        /// events, as the rejections of centrifuge-js run after its _disconnect.</summary>
        private void ClearOutgoingRequests()
        {
            if (_calls.Count == 0) return;
            var calls = _calls.Values.ToList();
            _calls.Clear();
            foreach (var call in calls) call.Timer?.Cancel();
            Loop.Defer(() =>
            {
                foreach (var call in calls) call.OnError(ConnectionClosedError(), false);
            });
        }

        private static CentrifugeException ConnectionClosedError() =>
            new CentrifugeException(CentrifugeErrorCodes.ConnectionClosed, "connection closed", false);

        private static CentrifugeException DisconnectedError() =>
            new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "client disconnected", false);

        private static CentrifugeException WriteError(Exception? cause) =>
            new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "transport write error", true, cause);

        // ---------------------------------------------------------------------------------------------
        // Pings and token refresh

        private void HandleServerPing()
        {
            ResetReconnectAttemptsIfPending();
            // Urgent: queued behind a batch, the pong might miss the server's deadline for it.
            if (_sendPong) AddCommand(new Command(), urgent: true);
        }

        private void WaitServerPing()
        {
            if (_state != CentrifugeClientState.Connected) return;
            ClearServerPingTimer();
            var delay = Utilities.PingDeadlineMilliseconds(_serverPing, _options.MaxServerPingDelay);
            var due = Clock.NowMs + delay;
            _serverPingTimer = LoopTimer.Start(Loop, delay, () =>
            {
                if (_state != CentrifugeClientState.Connected) return;
                // A ping may have been received already, not processed yet: let it be processed first. Any
                // data received re-arms this timer.
                var late = Clock.NowMs - due >= LateTimerMs;
                _serverPingTimer = LoopTimer.Start(Loop, late ? LateTimerGraceMs : 0, () =>
                {
                    if (_state != CentrifugeClientState.Connected) return;
                    DisconnectInternal(CentrifugeConnectingCodes.NoPing, "no ping", true);
                });
            });
        }

        /// <summary>Handles a missed server ping on <paramref name="session"/> when it is still the current
        /// one. Runs what the ping timer would; tests call it directly.</summary>
        internal void NoPing(ITransport session) =>
            Loop.Invoke(() =>
            {
                if (_state != CentrifugeClientState.Connected || !ReferenceEquals(_transport, session)) return;
                DisconnectInternal(CentrifugeConnectingCodes.NoPing, "no ping", true);
            });

        /// <summary>Completes once the steps queued before it ran (for tests).</summary>
        internal Task LoopBarrierAsync()
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() => Loop.Complete(() => done.TrySetResult(true)));
            return done.Task;
        }

        /// <summary>A subscribe timeout (for tests: what the timeout of a subscribe call does).</summary>
        internal void HandleSubscribeTimeout() =>
            Loop.Invoke(() => DisconnectInternal(CentrifugeConnectingCodes.SubscribeTimeout, "subscribe timeout", true));

        /// <summary>An unsubscribe error (for tests: what the error reply of an unsubscribe does).</summary>
        internal void HandleUnsubscribeError() =>
            Loop.Invoke(() => DisconnectInternal(CentrifugeConnectingCodes.UnsubscribeError, "unsubscribe error", true));

        /// <summary>The token refresh timer of a connected client (for tests).</summary>
        internal void RefreshConnectionToken() =>
            Loop.Invoke(() =>
            {
                if (_state == CentrifugeClientState.Connected) Refresh();
            });

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
            var connection = _connection;
            GetConnectionToken(token =>
            {
                if (connection != _connection) return;
                if (string.IsNullOrEmpty(token))
                {
                    FailUnauthorized();
                    return;
                }
                _token = token!;
                if (_state != CentrifugeClientState.Connected) return;

                var command = new Command { Refresh = new RefreshRequest { Token = _token } };
                RegisterCall(command, reply =>
                {
                    if (connection != _connection) return;
                    if (reply.Refresh != null) RefreshResponse(reply.Refresh);
                    // A reply breaking the protocol: retried as a temporary error.
                    else RefreshError(new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "refresh reply without result", true));
                }, (error, _) =>
                {
                    // A disconnect fails the pending command too: the connection it was for is gone.
                    if (connection == _connection) RefreshError(error);
                });
                AddCommand(command);
            }, error =>
            {
                if (connection != _connection || _state != CentrifugeClientState.Connected) return;
                if (error is CentrifugeUnauthorizedException)
                {
                    FailUnauthorized();
                    return;
                }
                EmitError("refreshToken", new CentrifugeException(CentrifugeErrorCodes.ClientRefreshToken, error.Message, true, error),
                    () => ScheduleRefresh(GetRefreshRetryDelay()));
            });
        }

        private void RefreshError(CentrifugeException error)
        {
            if (error.Code < 100 || error.Temporary)
            {
                EmitError("refresh", error, () => ScheduleRefresh(GetRefreshRetryDelay()));
            }
            else
            {
                DisconnectInternal(error.Code, error.Message, false);
            }
        }

        private static int GetRefreshRetryDelay() =>
            Utilities.CalculateBackoff(0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        private void RefreshResponse(RefreshResult result)
        {
            ClearRefreshTimer();
            if (result.Expires)
            {
                // The client id changes only if the reply carries one (Centrifugo never sets it).
                if (!string.IsNullOrEmpty(result.Client)) _clientId = result.Client;
                ScheduleRefresh(Utilities.TtlToMilliseconds(result.Ttl));
            }
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Cancel();
            _refreshTimer = null;
        }

        private void ClearReconnectTimer()
        {
            _reconnectTimer?.Cancel();
            _reconnectTimer = null;
        }

        private void ClearConnectTimer()
        {
            _connectTimer?.Cancel();
            _connectTimer = null;
        }

        private void ClearServerPingTimer()
        {
            _serverPingTimer?.Cancel();
            _serverPingTimer = null;
        }

        // ---------------------------------------------------------------------------------------------
        // Subscriptions

        /// <summary>The transport is open and connected (or a WebSocket was opened and the connect sent):
        /// subscribe commands can go out.</summary>
        internal bool TransportIsOpen => _transportIsOpen;

        internal bool IsConnected => _state == CentrifugeClientState.Connected;

        internal int TimeoutMilliseconds => TimeoutMs;

        /// <summary>Sends the subscribe commands of the subscriptions waiting for a connection: subscribing,
        /// nothing in flight, no resubscribe backoff running.</summary>
        private void SendSubscribeCommands()
        {
            foreach (var sub in _subscriptions.Values)
            {
                sub.SubscribeIfIdle();
            }
        }

        /// <summary>Sends the unsubscribe of <paramref name="channel"/> when the transport is open. An error
        /// reply or a timeout leaves the server state unknown: the client reconnects (as centrifuge-js).</summary>
        internal void SendUnsubscribe(string channel)
        {
            if (!_transportIsOpen) return;
            var command = new Command { Unsubscribe = new UnsubscribeRequest { Channel = channel } };
            var transportId = _transportId;
            RegisterCall(command, _ => { }, (_, _) =>
            {
                if (_transportId != transportId) return;
                DisconnectInternal(CentrifugeConnectingCodes.UnsubscribeError, "unsubscribe error", true);
            });
            AddCommand(command);
        }

        /// <summary>Maps the channel id of channel compaction to <paramref name="sub"/>.</summary>
        internal void SetSubscriptionId(CentrifugeSubscription sub, long oldId, long newId)
        {
            if (oldId > 0 && _subscriptionsById.TryGetValue(oldId, out var current) && ReferenceEquals(current, sub))
                _subscriptionsById.Remove(oldId);
            if (newId > 0) _subscriptionsById[newId] = sub;
        }

        private CentrifugeSubscription? GetSub(string channel, long id)
        {
            if (id > 0)
            {
                _subscriptionsById.TryGetValue(id, out var byId);
                return byId;
            }
            _subscriptions.TryGetValue(channel, out var sub);
            return sub;
        }

        /// <summary>
        /// The client-side subscription a push is for. The server allows one subscription per channel: while a
        /// server-side subscription has the channel, its pushes belong to it unless the client-side
        /// subscription is subscribed (e.g. the app kept an unsubscribed object of the channel).
        /// </summary>
        private CentrifugeSubscription? GetSubForPush(string channel, long id)
        {
            var sub = GetSub(channel, id);
            if (sub != null && channel.Length > 0 && _serverSubs.ContainsKey(channel) && sub.State != CentrifugeSubscriptionState.Subscribed)
                return null;
            return sub;
        }

        private void HandlePush(Push push)
        {
            var channel = push.Channel;
            if (push.Pub != null)
            {
                HandlePublication(channel, push.Pub, push.Id);
            }
            else if (push.Message != null)
            {
                Raise(Message, new CentrifugeMessageEventArgs(push.Message.Data.ToByteArray()), "message");
            }
            else if (push.Join != null)
            {
                var sub = GetSubForPush(channel, push.Id);
                if (sub == null)
                {
                    if (channel.Length > 0 && _serverSubs.ContainsKey(channel) && push.Join.Info != null)
                        Raise(Join, new CentrifugeJoinEventArgs(channel, CentrifugeClientInfo.FromProtocol(push.Join.Info)), "join");
                    return;
                }
                sub.HandleJoin(push.Join);
            }
            else if (push.Leave != null)
            {
                var sub = GetSubForPush(channel, push.Id);
                if (sub == null)
                {
                    if (channel.Length > 0 && _serverSubs.ContainsKey(channel) && push.Leave.Info != null)
                        Raise(Leave, new CentrifugeLeaveEventArgs(channel, CentrifugeClientInfo.FromProtocol(push.Leave.Info)), "leave");
                    return;
                }
                sub.HandleLeave(push.Leave);
            }
            else if (push.Unsubscribe != null)
            {
                HandleUnsubscribePush(channel, push.Unsubscribe);
            }
            else if (push.Subscribe != null)
            {
                var sub = push.Subscribe;
                _serverSubs[channel] = new ServerSubscription(sub.Offset, sub.Epoch, sub.Recoverable) { Subscribed = true };
                Raise(ServerSubscribed, ServerSubscribedArgs(channel, false, false, sub.Recoverable, sub.Positioned,
                    sub.Offset, sub.Epoch, sub.Data), "serverSubscribed");
            }
            else if (push.Disconnect != null)
            {
                // The server pushed it over the transport, so the transport works.
                _transportWasOpen = true;
                var code = (int)push.Disconnect.Code;
                DisconnectInternal(code, push.Disconnect.Reason, !IsPermanentDisconnectCode(code));
            }
        }

        private void HandleUnsubscribePush(string channel, Unsubscribe unsubscribe)
        {
            var sub = GetSubForPush(channel, 0);
            if (sub == null)
            {
                if (channel.Length > 0 && _serverSubs.Remove(channel))
                    Raise(ServerUnsubscribed, new CentrifugeServerUnsubscribedEventArgs(channel), "serverUnsubscribed");
                return;
            }
            sub.HandleUnsubscribePush((int)unsubscribe.Code, unsubscribe.Reason);
        }

        private void HandlePublication(string channel, Publication pub, long id)
        {
            var sub = GetSubForPush(channel, id);
            if (sub == null)
            {
                // A compacted push with an unknown id has no channel: dropped.
                if (channel.Length > 0 && _serverSubs.ContainsKey(channel)) HandleServerPublication(channel, pub);
                return;
            }
            sub.HandlePublication(pub);
        }

        /// <summary>
        /// A server-side publication. The position moves after the app has it, so the position it can observe
        /// never runs ahead of what it received; a connect built from its handler recovers after it.
        /// </summary>
        private void HandleServerPublication(string channel, Publication pub)
        {
            var args = CreatePublicationArgs(channel, pub);
            if (pub.Offset > 0) _pendingServerSubOffsets[channel] = pub.Offset;
            bool failed;
            try
            {
                failed = Raise(Publication, args, "publication");
            }
            finally
            {
                _pendingServerSubOffsets.Remove(channel);
            }
            // A publication whose handler threw isn't delivered: the position stays before it, so the
            // next recovery delivers it again.
            if (failed) return;
            if (!_serverSubs.TryGetValue(channel, out var serverSub)) return;
            if (pub.Offset > 0) serverSub.Offset = pub.Offset;
            // The epoch of a channel that had no stream at subscribe time comes with its first publication.
            if (!string.IsNullOrEmpty(pub.Epoch)) serverSub.Epoch = pub.Epoch;
        }

        /// <summary>A publication that fails to decode (delta) means a corrupt stream: the client disconnects
        /// (as centrifuge-js on an exception while handling a reply).</summary>
        internal void HandleUndecodablePublication(string message) =>
            DisconnectInternal(CentrifugeDisconnectedCodes.BadProtocol, "exception during message handling: " + message, false);

        // ---------------------------------------------------------------------------------------------
        // Events

        /// <summary>
        /// Raises one of this object's events through <see cref="EventDispatch.Raise{TArgs}"/>. Returns
        /// whether a handler threw, which stopped the client.
        /// </summary>
        private bool Raise<TArgs>(EventHandler<TArgs>? handler, TArgs args, string type) =>
            HandlerFailed(EventDispatch.Raise(this, handler, args, type, Error, _logger));

        private void EmitErrorEvent(string type, Exception exception) =>
            HandlerFailed(EventDispatch.RaiseError(this, Error, type, exception, _logger));

        /// <summary>The number of exceptions escaped from event handlers (see <see cref="HandlerFailed"/>).</summary>
        internal int HandlerFailures { get; private set; }

        /// <summary>
        /// An exception escaping an application event handler (already reported through Error). The SDK
        /// can't know what the handler left undone, so it fails loudly instead of going on as if nothing
        /// happened: the client disconnects with BadProtocol and doesn't reconnect. Its own processing
        /// continues as if the handler had returned, as for a handler calling Disconnect(), so no state
        /// change is left half done. Returns whether there was an exception.
        /// </summary>
        internal bool HandlerFailed(Exception? exception)
        {
            if (exception == null) return false;
            HandlerFailures++;
            if (_state != CentrifugeClientState.Disconnected)
            {
                DisconnectInternal(CentrifugeDisconnectedCodes.BadProtocol, "exception in event handler: " + exception.Message, false);
            }
            return true;
        }

        internal static CentrifugePublicationEventArgs CreatePublicationArgs(string channel, Publication pub)
        {
            return CreatePublicationArgs(channel, pub, pub.Data.ToByteArray());
        }

        internal static CentrifugePublicationEventArgs CreatePublicationArgs(string channel, Publication pub, byte[] data)
        {
            CentrifugeClientInfo? info = pub.Info != null ? CentrifugeClientInfo.FromProtocol(pub.Info) : null;

            var tags = pub.Tags.Count > 0
                ? new Dictionary<string, string>(pub.Tags)
                : null;

            return new CentrifugePublicationEventArgs(
                channel,
                data,
                info,
                pub.Offset > 0 ? pub.Offset : null,
                tags
            );
        }

        // ---------------------------------------------------------------------------------------------
        // Disposal

        /// <summary>
        /// Asynchronously disposes the client: every subscription is unsubscribed (ClientClosed), the client
        /// disconnects, and the transport is closed. Called from an event handler it doesn't wait for the
        /// close (the handler holds the loop).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var inStep = Loop.InStep;
            var closed = Close();
            if (inStep) return;
            try
            {
                await (await closed.ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch
            {
                // Closing errors are ignored.
            }
        }

        /// <summary>
        /// Disposes the client: every subscription is unsubscribed (ClientClosed), the client disconnects, and
        /// the transport is closed — waited for, except from an event handler (the handler holds the loop) and
        /// in the browser (a blocking wait would deadlock the single thread). Prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var inStep = Loop.InStep;
            var closed = Close();
            if (inStep || IsBrowser) return;
            try
            {
                closed.ConfigureAwait(false).GetAwaiter().GetResult().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch
            {
                // Closing errors are ignored.
            }
        }

        /// <summary>The close step; its task gives the task of the transport close.</summary>
        private Task<Task> Close()
        {
            var closed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Invoke(() =>
            {
                // Removed before its Unsubscribed event: a handler may create a new subscription of the channel.
                foreach (var sub in _subscriptions.Values.ToList())
                {
                    Unregister(sub);
                    sub.CloseForClient();
                }
                DisconnectInternal(CentrifugeDisconnectedCodes.DisconnectCalled, "disconnect called", false);
                var transportClose = _lastTransportClose;
                Loop.Complete(() => closed.TrySetResult(transportClose));
            });
            return closed.Task;
        }
    }

    /// <summary>A server-side subscription's registry entry (loop-owned).</summary>
    internal sealed class ServerSubscription
    {
        public ServerSubscription(ulong offset, string epoch, bool recoverable)
        {
            Offset = offset;
            Epoch = epoch;
            Recoverable = recoverable;
        }

        public ulong Offset { get; set; }

        public string Epoch { get; set; }

        public bool Recoverable { get; }

        /// <summary>Its ServerSubscribed was raised, and no ServerSubscribing since.</summary>
        public bool Subscribed { get; set; }
    }
}
