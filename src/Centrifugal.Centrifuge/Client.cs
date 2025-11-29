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
    /// Represents a server-side subscription.
    /// </summary>
    internal class ServerSubscription
    {
        public ulong Offset { get; set; }
        public string Epoch { get; set; } = string.Empty;
        public bool Recoverable { get; set; }
    }

    /// <summary>
    /// Centrifuge client for real-time messaging with Centrifugo server.
    /// </summary>
    public class CentrifugeClient : IDisposable, IAsyncDisposable
    {
        private readonly string? _endpoint;
        private readonly List<CentrifugeTransportEndpoint>? _transportEndpoints;
        private readonly CentrifugeClientOptions _options;
        private readonly ConcurrentDictionary<string, CentrifugeSubscription> _subscriptions = new();
        private readonly ConcurrentDictionary<string, ServerSubscription> _serverSubscriptions = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Reply>> _pendingCalls = new();
        private readonly SemaphoreSlim _stateLock = new SemaphoreSlim(1, 1);
        private readonly object _stateChangeLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _readyPromises = new();
        private readonly List<Command> _commandBatch = new();
        private readonly object _commandBatchLock = new object();

        private ITransport? _transport;
        private CentrifugeClientState _state = CentrifugeClientState.Disconnected;
        private int _commandId;
        private int _reconnectAttempts;
        private CancellationTokenSource? _reconnectCts;

        // Internal property to allow Subscription to access timeout
        internal TimeSpan Timeout => _options.Timeout;
        private Timer? _pingTimer;
        private Timer? _refreshTimer;
        private uint _serverPingInterval;
        private bool _sendPong;
        private string? _clientId;
        private string _session = string.Empty;
        private string _node = string.Empty;
        private Command? _pendingConnectCommand; // For emulation transports, stores the connect command sent in initialData
        private bool _disposed;
        private int _refreshAttempts;
        private bool _refreshRequired;
        private int _currentTransportIndex;
        private bool _transportWasOpen;
        private int _promiseId;
        private bool _transportIsOpen;
        private Timer? _commandBatchTimer;
        private bool _commandBatchPending;
        private int _commandBatchSize;
#if NET6_0_OR_GREATER
        private static Microsoft.JSInterop.IJSRuntime? _globalJSRuntime;
        private readonly Microsoft.JSInterop.IJSRuntime? _jsRuntime;
#endif
        private readonly ILogger? _logger;

        /// <summary>
        /// Maximum size of a command batch in bytes (15KB).
        /// When batch exceeds this size, it will be flushed immediately.
        /// </summary>
        private const int MaxCommandBatchSize = 15 * 1024;

        /// <summary>
        /// Command batching delay in milliseconds.
        /// Commands sent within this window will be automatically batched together.
        /// </summary>
        private const int CommandBatchDelayMs = 1;

        /// <summary>
        /// Gets the current client state.
        /// </summary>
        public CentrifugeClientState State => _state;

        /// <summary>
        /// Gets whether the transport is currently open.
        /// </summary>
        internal bool TransportIsOpen => _transportIsOpen;

        /// <summary>
        /// Event raised when client state changes.
        /// </summary>
        /// <remarks>
        /// <para><b>Best Practice:</b> Keep event handlers fast to avoid blocking the real-time message processing pipeline.</para>
        /// <para><b>Async Operations:</b> Use <c>async void</c> with <c>await</c> for I/O operations.</para>
        /// <para><b>CRITICAL:</b> Never block on SDK async methods (e.g., <c>PublishAsync().Wait()</c>) - this will cause deadlock!</para>
        /// <para>Blocking on non-SDK operations (database calls, file I/O, etc.) is safe but not recommended for performance.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // BEST ✓ - Async I/O operations
        /// client.StateChanged += async (sender, e) => {
        ///     await LogToFileAsync(e.NewState);
        /// };
        ///
        /// // OK - Synchronous operations on other libraries (won't deadlock, but may be slow)
        /// client.StateChanged += (sender, e) => {
        ///     File.WriteAllText("state.txt", e.NewState.ToString());  // Safe but blocks thread
        /// };
        ///
        /// // DEADLOCK ✗ - Never block on SDK methods!
        /// client.StateChanged += (sender, e) => {
        ///     client.RpcAsync("method", data).Wait();  // DEADLOCK!
        /// };
        /// </code>
        /// </example>
        public event EventHandler<CentrifugeStateEventArgs>? StateChanged;

        /// <summary>
        /// Event raised when client is connecting or reconnecting.
        /// </summary>
        /// <remarks>
        /// <para><b>Best Practice:</b> Keep handlers fast. Use <c>async void</c> with <c>await</c> for I/O operations.</para>
        /// <para><b>CRITICAL:</b> Never block on SDK async methods - this will cause deadlock!</para>
        /// </remarks>
        public event EventHandler<CentrifugeConnectingEventArgs>? Connecting;

        /// <summary>
        /// Event raised when client successfully connects to the server.
        /// This is a good place to set up subscriptions or send initial data.
        /// </summary>
        /// <remarks>
        /// <para><b>Best Practice:</b> Keep handlers fast to avoid delaying message processing.</para>
        /// <para><b>Async SDK Methods:</b> You can safely call <c>PublishAsync</c>, <c>RpcAsync</c>, etc. using <c>await</c> in an <c>async void</c> handler.</para>
        /// <para><b>CRITICAL:</b> Never use <c>.Wait()</c>, <c>.Result</c>, or <c>.GetAwaiter().GetResult()</c> on SDK async methods - this will cause deadlock!</para>
        /// <para>Handlers are dispatched to the thread pool, so blocking on non-SDK operations (like database calls) won't deadlock but may impact performance.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // BEST ✓ - Async SDK operations with await
        /// client.Connected += async (sender, e) => {
        ///     Console.WriteLine($"Connected! Client ID: {e.ClientId}");
        ///     var sub = client.NewSubscription("chat");
        ///     sub.Subscribe();
        ///     await sub.ReadyAsync();
        ///     await sub.PublishAsync(Encoding.UTF8.GetBytes("Hello!"));  // Safe!
        /// };
        ///
        /// // OK - Synchronous non-SDK work (safe but blocks thread pool thread)
        /// client.Connected += (sender, e) => {
        ///     database.UpdateConnectionStatus(e.ClientId);  // Won't deadlock
        /// };
        ///
        /// // DEADLOCK ✗ - Never block on SDK async methods!
        /// client.Connected += (sender, e) => {
        ///     sub.PublishAsync(data).Wait();  // DEADLOCK! Use 'async/await' instead
        /// };
        /// </code>
        /// </example>
        public event EventHandler<CentrifugeConnectedEventArgs>? Connected;

        /// <summary>
        /// Event raised when client is disconnected from the server.
        /// </summary>
        /// <remarks>
        /// <para><b>Best Practice:</b> Keep handlers fast. Use <c>async void</c> with <c>await</c> for I/O operations.</para>
        /// <para><b>Note:</b> Don't block on SDK async methods - will cause deadlock. Blocking on non-SDK operations is safe but impacts performance.</para>
        /// </remarks>
        public event EventHandler<CentrifugeDisconnectedEventArgs>? Disconnected;

        /// <summary>
        /// Event raised when an error occurs. Mostly for logging purposes.
        /// </summary>
        /// <remarks>
        /// <para><b>Best Practice:</b> Keep handlers fast. Use <c>async void</c> with <c>await</c> for I/O operations.</para>
        /// <para><b>Exception Handling:</b> Exceptions in <c>async void</c> handlers cannot be caught by the SDK.
        /// Always use try-catch in your handlers to prevent application crashes.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// client.Error += async (sender, e) => {
        ///     try {
        ///         await LogErrorAsync(e.Type, e.Message);
        ///     }
        ///     catch (Exception ex) {
        ///         Console.WriteLine($"Logging failed: {ex.Message}");
        ///     }
        /// };
        /// </code>
        /// </example>
        public event EventHandler<CentrifugeErrorEventArgs>? Error;

        /// <summary>
        /// Event raised when a message is received from server.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugeMessageEventArgs>? Message;

        /// <summary>
        /// Event raised for server-side subscription publications.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugePublicationEventArgs>? Publication;

        /// <summary>
        /// Event raised for server-side subscription join events.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugeJoinEventArgs>? Join;

        /// <summary>
        /// Event raised for server-side subscription leave events.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugeLeaveEventArgs>? Leave;

        /// <summary>
        /// Event raised when server-side subscription is subscribing.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugeServerSubscribingEventArgs>? ServerSubscribing;

        /// <summary>
        /// Event raised when server-side subscription is subscribed.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
        public event EventHandler<CentrifugeServerSubscribedEventArgs>? ServerSubscribed;

        /// <summary>
        /// Event raised when server-side subscription is unsubscribed.
        /// </summary>
        /// <remarks>
        /// Keep handlers fast. Don't block on SDK async methods (will deadlock). Use <c>async void</c> with <c>await</c> for I/O.
        /// </remarks>
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
        /// Initializes a new instance of the <see cref="CentrifugeClient"/> class.
        /// </summary>
        /// <param name="endpoint">WebSocket endpoint URL.</param>
        /// <param name="options">Client options.</param>
        public CentrifugeClient(string endpoint, CentrifugeClientOptions? options = null)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be empty", nameof(endpoint));
            }

            _endpoint = endpoint;
            _options = options ?? new CentrifugeClientOptions();
            _options.Validate();
            _logger = _options.Logger;
#if NET6_0_OR_GREATER
            _jsRuntime = _options.JSRuntime ?? _globalJSRuntime;
#endif
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeClient"/> class with multi-transport fallback.
        /// </summary>
        /// <param name="transportEndpoints">Array of transport endpoints to try in order.</param>
        /// <param name="options">Client options.</param>
        public CentrifugeClient(CentrifugeTransportEndpoint[] transportEndpoints, CentrifugeClientOptions? options = null)
        {
            if (transportEndpoints == null || transportEndpoints.Length == 0)
            {
                throw new ArgumentException("Transport endpoints cannot be null or empty", nameof(transportEndpoints));
            }

            _transportEndpoints = new List<CentrifugeTransportEndpoint>(transportEndpoints);
            _options = options ?? new CentrifugeClientOptions();
            _options.Validate();
            _logger = _options.Logger;
#if NET6_0_OR_GREATER
            _jsRuntime = _options.JSRuntime ?? _globalJSRuntime;
#endif
        }

        /// <summary>
        /// Connects to the Centrifugo server. This method returns immediately and starts the connection process in the background.
        /// Use ReadyAsync() to wait for the connection to be established, or use the Connected event.
        /// </summary>
        public void Connect()
        {
            if (_state == CentrifugeClientState.Connected)
            {
                return;
            }

            if (_state == CentrifugeClientState.Connecting)
            {
                return;
            }

            _reconnectAttempts = 0;
            StartConnecting();
        }

        /// <summary>
        /// Disconnects from the Centrifugo server. This method returns immediately and starts the disconnection process in the background.
        /// </summary>
        public void Disconnect()
        {
            _ = SetDisconnectedAsync(CentrifugeDisconnectedCodes.DisconnectCalled, "disconnect called", false);
        }

        /// <summary>
        /// Returns a Task that completes when the client is connected.
        /// If the client is already connected, the Task completes immediately.
        /// If the client is disconnected, the Task is rejected.
        /// </summary>
        /// <param name="timeout">Optional timeout.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes when connected.</returns>
        public Task ReadyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            switch (_state)
            {
                case CentrifugeClientState.Disconnected:
                    return Task.FromException(new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "client disconnected"));

                case CentrifugeClientState.Connected:
                    return Task.CompletedTask;

                default:
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var promiseId = NextPromiseId();
                    _readyPromises[promiseId] = tcs;

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
        }


        /// <summary>
        /// Creates a new subscription to a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="options">Subscription options.</param>
        /// <returns>The subscription instance.</returns>
        public CentrifugeSubscription NewSubscription(string channel, CentrifugeSubscriptionOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentException("Channel cannot be null or empty", nameof(channel));
            }

            if (_subscriptions.ContainsKey(channel))
            {
                throw new CentrifugeDuplicateSubscriptionException(channel);
            }

            var subscription = new CentrifugeSubscription(this, channel, options);
            _subscriptions[channel] = subscription;
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
        /// Removes a subscription.
        /// </summary>
        /// <param name="subscription">The subscription to remove.</param>
        public void RemoveSubscription(CentrifugeSubscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));

            if (subscription.State != CentrifugeSubscriptionState.Unsubscribed)
            {
                subscription.Unsubscribe();
            }

            _subscriptions.TryRemove(subscription.Channel, out _);
        }


        /// <summary>
        /// Gets all subscriptions.
        /// </summary>
        public IReadOnlyDictionary<string, CentrifugeSubscription> Subscriptions => _subscriptions;

        /// <summary>
        /// Sets the connection token. Can be used to update token or reset to empty.
        /// </summary>
        /// <param name="token">New connection token (JWT).</param>
        public void SetToken(string? token)
        {
            lock (_stateChangeLock)
            {
                _options.Token = token;
            }
        }

        /// <summary>
        /// Sets the connection data. This will be used for all subsequent connection attempts.
        /// The data is copied internally to prevent external modifications.
        /// </summary>
        /// <param name="data">New connection data.</param>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            lock (_stateChangeLock)
            {
                _options.Data = data.IsEmpty ? default : data.ToArray();
            }
        }

        /// <summary>
        /// Sets the connection headers (emulated headers sent with first protocol message).
        /// Requires Centrifugo v6+.
        /// The headers dictionary is copied internally to prevent external modifications.
        /// </summary>
        /// <param name="headers">Headers to set.</param>
        public void SetHeaders(Dictionary<string, string>? headers)
        {
            lock (_stateChangeLock)
            {
                _options.Headers = headers != null ? new Dictionary<string, string>(headers) : null;
            }
        }

        /// <summary>
        /// Sends an RPC call to the server.
        /// Automatically waits for the client to be connected before sending.
        /// </summary>
        /// <param name="method">RPC method name.</param>
        /// <param name="data">Request data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>RPC result.</returns>
        public async Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            // Wait for client to be ready
            await ReadyAsync(_options.Timeout, cancellationToken).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = NextCommandId(),
                Rpc = new RPCRequest
                {
                    Method = method,
                    Data = ByteString.CopyFrom(data.Span)
                }
            };

            var reply = await SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

            if (reply.Error != null)
            {
                throw new CentrifugeException(
                    (int)reply.Error.Code,
                    reply.Error.Message,
                    reply.Error.Temporary
                );
            }

            return new CentrifugeRpcResult(reply.Rpc?.Data.ToByteArray() ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Sends an asynchronous message to the server (no response expected).
        /// Automatically waits for the client to be connected before sending.
        /// </summary>
        /// <param name="data">Message data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            // Wait for client to be ready
            await ReadyAsync(_options.Timeout, cancellationToken).ConfigureAwait(false);

            var cmd = new Command
            {
                Send = new SendRequest
                {
                    Data = ByteString.CopyFrom(data.Span)
                }
            };

            if (_transport == null || _state != CentrifugeClientState.Connected)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "Client is not connected");
            }

            await _transport.SendAsync(cmd.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets presence information for a channel.
        /// Automatically waits for the client to be connected before sending.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence result.</returns>
        public async Task<CentrifugePresenceResult> PresenceAsync(string channel, CancellationToken cancellationToken = default)
        {
            // Wait for client to be ready
            await ReadyAsync(_options.Timeout).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = NextCommandId(),
                Presence = new PresenceRequest
                {
                    Channel = channel
                }
            };

            var reply = await SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

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
        /// Gets presence stats for a channel.
        /// Automatically waits for the client to be connected before sending.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence stats result.</returns>
        public async Task<CentrifugePresenceStatsResult> PresenceStatsAsync(string channel, CancellationToken cancellationToken = default)
        {
            // Wait for client to be ready
            await ReadyAsync(_options.Timeout).ConfigureAwait(false);

            var cmd = new Command
            {
                Id = NextCommandId(),
                PresenceStats = new PresenceStatsRequest
                {
                    Channel = channel
                }
            };

            var reply = await SendCommandAsync(cmd, cancellationToken).ConfigureAwait(false);

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

        internal async Task<Reply> SendCommandAsync(Command command, CancellationToken cancellationToken)
        {
            if (_transport == null || (_state != CentrifugeClientState.Connected && _state != CentrifugeClientState.Connecting))
            {
                throw new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "Client is not connected");
            }

            var tcs = new TaskCompletionSource<Reply>();
            _pendingCalls[command.Id] = tcs;

            try
            {
                // Check if this is not a connect command (which must be sent immediately)
                // All other commands are automatically batched
                bool isConnectCommand = command.Connect != null;
                bool shouldBatch = !isConnectCommand;

                if (shouldBatch)
                {
                    // Add command to batch
                    ScheduleCommandBatch(command);
                }
                else
                {
                    // Send immediately for connect commands or when batching is disabled
                    await SendCommandsImmediateAsync(new[] { command }, cancellationToken).ConfigureAwait(false);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.Timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(System.Threading.Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != tcs.Task)
                {
                    throw new CentrifugeTimeoutException();
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pendingCalls.TryRemove(command.Id, out _);
            }
        }

        private async Task SendCommandsImmediateAsync(IEnumerable<Command> commands, CancellationToken cancellationToken)
        {
            if (_transport == null)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "Client is not connected");
            }

            var firstCommand = commands.FirstOrDefault();
            if (firstCommand == null)
            {
                return;
            }

            // Don't send connect command for emulation transports - it was already sent during OpenAsync
            bool isConnectCommand = firstCommand.Connect != null;

            if (!isConnectCommand || !_transport.UsesEmulation)
            {
                if (_transport.UsesEmulation)
                {
                    // For emulation transports, we need to pre-wrap commands with varint delimiters
                    // because SendEmulationAsync doesn't add them
                    using var ms = new MemoryStream();
                    foreach (var cmd in commands)
                    {
                        VarintCodec.WriteDelimitedMessage(ms, cmd.ToByteArray());
                    }
                    var delimitedCommands = ms.ToArray();

                    var emulationEndpoint = GetEmulationEndpoint();
                    await _transport.SendEmulationAsync(delimitedCommands, _session, _node, emulationEndpoint, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // For WebSocket: prepare data with varint delimiters, then send
                    using var ms = new MemoryStream();
                    var commandsList = commands.ToList();
                    foreach (var cmd in commandsList)
                    {
                        VarintCodec.WriteDelimitedMessage(ms, cmd.ToByteArray());
                    }
                    var delimitedData = ms.ToArray();

                    if (commandsList.Count > 1)
                    {
                        _logger?.LogDebug($"Sending {commandsList.Count} commands in single frame ({delimitedData.Length} bytes)");
                    }

                    await _transport.SendAsync(delimitedData, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void ScheduleCommandBatch(Command command)
        {
            lock (_commandBatchLock)
            {
                var commandBytes = command.ToByteArray();
                var estimatedSize = commandBytes.Length + 10; // +10 for varint overhead

                // Add command to batch
                _commandBatch.Add(command);
                _commandBatchSize += estimatedSize;

                // If batch size exceeds limit, flush immediately
                if (_commandBatchSize >= MaxCommandBatchSize)
                {
                    // Cancel pending timer
                    _commandBatchTimer?.Dispose();
                    _commandBatchTimer = null;
                    _commandBatchPending = false;

                    // Flush synchronously
                    _ = Task.Run(async () => await FlushCommandBatchAsync().ConfigureAwait(false));
                    return;
                }

                // Schedule flush if not already pending
                if (!_commandBatchPending)
                {
                    _commandBatchPending = true;

                    _commandBatchTimer?.Dispose();
                    _commandBatchTimer = new Timer(_ =>
                    {
                        lock (_commandBatchLock)
                        {
                            _commandBatchPending = false;
                        }
                        _ = Task.Run(async () => await FlushCommandBatchAsync().ConfigureAwait(false));
                    }, null, TimeSpan.FromMilliseconds(CommandBatchDelayMs), System.Threading.Timeout.InfiniteTimeSpan);
                }
            }
        }

        private async Task FlushCommandBatchAsync()
        {
            List<Command> commandsToSend;

            lock (_commandBatchLock)
            {
                // Check client state before taking commands from batch
                if (_state != CentrifugeClientState.Connected && _state != CentrifugeClientState.Connecting)
                {
                    return;
                }

                if (!_transportIsOpen)
                {
                    return;
                }

                if (_commandBatch.Count == 0)
                {
                    return;
                }

                // Take all commands from batch
                commandsToSend = new List<Command>(_commandBatch);
                _commandBatch.Clear();
                _commandBatchSize = 0;
            }

            try
            {
                await SendCommandsImmediateAsync(commandsToSend, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Error flushing command batch: {ex.Message}");
                // Errors will be handled by individual command timeouts
            }
        }

        private string GetEmulationEndpoint()
        {
            if (!string.IsNullOrEmpty(_options.EmulationEndpoint))
            {
                return _options.EmulationEndpoint;
            }

            // Auto-construct emulation endpoint from transport endpoint
            // Emulation endpoint is at root level: http://host:port/emulation
            string endpoint = _endpoint ?? _transportEndpoints?[_currentTransportIndex].Endpoint ?? throw new CentrifugeConfigurationException("No endpoint configured");

            var uri = new Uri(endpoint);
            return $"{uri.Scheme}://{uri.Authority}/emulation";
        }

        private void StartConnecting()
        {
            SetState(CentrifugeClientState.Connecting);
            Connecting?.Invoke(this, new CentrifugeConnectingEventArgs(CentrifugeConnectingCodes.ConnectCalled, "connect called"));

            _ = Task.Run(async () =>
            {
                try
                {
                    await CreateTransportAsync().ConfigureAwait(false);
                }
                catch (CentrifugeUnauthorizedException ex)
                {
                    // Unauthorized exception should stop connection attempts permanently
                    _logger?.LogDebug($"Caught CentrifugeUnauthorizedException in StartConnecting: {ex.Message}");
                    await SetDisconnectedAsync(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // If using multi-transport fallback and transport failed before opening,
                    // try the next transport in the list
                    if (_transportEndpoints != null && !_transportWasOpen)
                    {
                        _currentTransportIndex++;
                        if (_currentTransportIndex >= _transportEndpoints.Count)
                        {
                            _currentTransportIndex = 0;
                        }
                    }

                    OnError("transport", ex);
                    await ScheduleReconnectAsync().ConfigureAwait(false);
                }
            });
        }

        private async Task StartConnectingAsync(int code, string reason)
        {
            SetState(CentrifugeClientState.Connecting);
            Connecting?.Invoke(this, new CentrifugeConnectingEventArgs(code, reason));

            try
            {
                await CreateTransportAsync().ConfigureAwait(false);
            }
            catch (CentrifugeUnauthorizedException ex)
            {
                // Unauthorized exception should stop connection attempts permanently
                _logger?.LogDebug($"Caught CentrifugeUnauthorizedException in StartConnectingAsync: {ex.Message}");
                await SetDisconnectedAsync(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // If using multi-transport fallback and transport failed before opening,
                // try the next transport in the list
                if (_transportEndpoints != null && !_transportWasOpen)
                {
                    _currentTransportIndex++;
                    if (_currentTransportIndex >= _transportEndpoints.Count)
                    {
                        _currentTransportIndex = 0;
                    }
                }

                OnError("transport", ex);
                await ScheduleReconnectAsync().ConfigureAwait(false);
            }
        }

        private async Task CreateTransportAsync()
        {
            // Unsubscribe from old transport events before disposing to prevent race conditions
            if (_transport != null)
            {
                _transport.Opened -= OnTransportOpened;
                _transport.MessageReceived -= OnTransportMessage;
                _transport.Closed -= OnTransportClosed;
                _transport.Error -= OnTransportError;
                _transport.Dispose();
                _transport = null;
            }

            ITransport transport;

            // Multi-transport fallback mode
            if (_transportEndpoints != null)
            {
                // Move to next transport if we've exceeded the list
                if (_currentTransportIndex >= _transportEndpoints.Count)
                {
                    _currentTransportIndex = 0;
                }

                // Try transports until we find a supported one
                int attempts = 0;
                while (attempts < _transportEndpoints.Count)
                {
                    var transportConfig = _transportEndpoints[_currentTransportIndex];

                    try
                    {
                        transport = CreateTransport(transportConfig.Transport, transportConfig.Endpoint);
                        _transport = transport;
                        break;
                    }
                    catch (CentrifugeConfigurationException)
                    {
                        // Unsupported transport, try next one
                        _currentTransportIndex++;
                        if (_currentTransportIndex >= _transportEndpoints.Count)
                        {
                            _currentTransportIndex = 0;
                        }
                        attempts++;
                    }
                }

                if (_transport == null)
                {
                    throw new CentrifugeConfigurationException("No supported transport found in the transport endpoints list");
                }
            }
            // Single endpoint mode.
            else if (_endpoint != null)
            {
                // Determine transport type from endpoint
                if (_endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                    _endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the same logic as CreateTransport to properly select browser vs native transport
                    transport = CreateTransport(CentrifugeTransportType.WebSocket, _endpoint);
                    _transport = transport;
                }
                else if (_endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         _endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // Support HTTP streaming endpoint as well
                    transport = CreateTransport(CentrifugeTransportType.HttpStream, _endpoint);
                    _transport = transport;
                }
                else
                {
                    throw new CentrifugeConfigurationException("Endpoint must start with ws://, wss://, http://, or https://");
                }
            }
            else
            {
                throw new CentrifugeConfigurationException("No endpoint configured");
            }

            _transport.Opened += OnTransportOpened;
            _transport.MessageReceived += OnTransportMessage;
            _transport.Closed += OnTransportClosed;
            _transport.Error += OnTransportError;

            // For emulation transports, build connect command and send it during OpenAsync
            byte[]? initialData = null;
            TaskCompletionSource<Reply>? connectTcs = null;
            if (_transport.UsesEmulation)
            {
                // Build and store the connect command for later use in SendConnectCommandAsync
                _pendingConnectCommand = await BuildConnectCommandObjectAsync().ConfigureAwait(false);
                initialData = _pendingConnectCommand.ToByteArray();

                // Register the pending call BEFORE opening the transport to avoid race condition
                // where reply arrives before registration
                connectTcs = new TaskCompletionSource<Reply>();
                _pendingCalls[_pendingConnectCommand.Id] = connectTcs;
            }

            await _transport.OpenAsync(initialData: initialData).ConfigureAwait(false);
        }

        private ITransport CreateTransport(CentrifugeTransportType transportType, string endpoint)
        {
            switch (transportType)
            {
                case CentrifugeTransportType.WebSocket:
#if NET6_0_OR_GREATER
                    // Use browser WebSocket transport if IJSRuntime is provided or running in browser
                    if (_jsRuntime != null || OperatingSystem.IsBrowser())
                    {
                        if (_jsRuntime == null)
                        {
                            throw new CentrifugeConfigurationException(
                                "Running in browser environment but IJSRuntime not provided. " +
                                "Either call CentrifugeClient.InitializeBrowserInterop(jsRuntime) at application startup, " +
                                "or pass IJSRuntime via CentrifugeClientOptions.JSRuntime.");
                        }
                        return new BrowserWebSocketTransport(endpoint, _jsRuntime, _logger);
                    }
#endif
                    return new WebSocketTransport(endpoint);
                case CentrifugeTransportType.HttpStream:
#if NET6_0_OR_GREATER
                    // Use browser HTTP stream transport if IJSRuntime is provided or running in browser
                    if (_jsRuntime != null || OperatingSystem.IsBrowser())
                    {
                        if (_jsRuntime == null)
                        {
                            throw new CentrifugeConfigurationException(
                                "Running in browser environment but IJSRuntime not provided. " +
                                "Either call CentrifugeClient.InitializeBrowserInterop(jsRuntime) at application startup, " +
                                "or pass IJSRuntime via CentrifugeClientOptions.JSRuntime.");
                        }
                        return new BrowserHttpStreamTransport(endpoint, _jsRuntime, _logger);
                    }
#endif
                    return new HttpStreamTransport(endpoint);
                default:
                    throw new CentrifugeConfigurationException($"Unsupported transport type: {transportType}");
            }
        }

        private async void OnTransportOpened(object? sender, EventArgs e)
        {
            _logger?.LogDebug("OnTransportOpened called");
            // Defensive check: don't process events if client is disposed
            if (_disposed) return;

            // Mark that at least one transport successfully opened
            _transportWasOpen = true;

            try
            {
                _logger?.LogDebug("Sending connect command...");
                await SendConnectCommandAsync().ConfigureAwait(false);
                _logger?.LogDebug("Connect command completed successfully");
            }
            catch (CentrifugeTimeoutException ex)
            {
                _logger?.LogDebug($"Connect timeout: {ex.Message}");
                // Connect timeout should trigger reconnect, not permanent disconnect
                OnError("connect", new CentrifugeException(CentrifugeErrorCodes.Timeout, "connect timeout", true));
                _transport?.Dispose();
                _transport = null;
                await ScheduleReconnectAsync().ConfigureAwait(false);
            }
            catch (CentrifugeException ex)
            {
                _logger?.LogDebug($"CentrifugeException in connect: Code={ex.Code}, Message={ex.Message}, Temporary={ex.Temporary}");
                // Error code 109 (token expired) or temporary errors should trigger reconnect
                if (ex.Code == 109 || ex.Code < 100 || ex.Temporary)
                {
                    _logger?.LogDebug("Triggering reconnect due to temporary error");
                    OnError("connect", ex);
                    _transport?.Dispose();
                    _transport = null;
                    await ScheduleReconnectAsync().ConfigureAwait(false);
                }
                else
                {
                    _logger?.LogDebug("Permanent error, disconnecting");
                    // Permanent error - disconnect
                    OnError("connect", ex);
                    await SetDisconnectedAsync(ex.Code, ex.Message, false).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"General exception in connect: {ex.GetType().Name}: {ex.Message}");
                // General exceptions during connect should trigger reconnect (e.g., GetToken errors)
                OnError("connect", ex);
                _transport?.Dispose();
                _transport = null;
                await ScheduleReconnectAsync().ConfigureAwait(false);
            }
        }

        private async Task<Command> BuildConnectCommandObjectAsync()
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
                    token = await _options.GetToken().ConfigureAwait(false);
                    lock (_stateChangeLock)
                    {
                        _options.Token = token;
                    }
                }
                catch (CentrifugeUnauthorizedException)
                {
                    throw;
                }
            }

            ReadOnlyMemory<byte> data;
            Dictionary<string, string>? headers;
            lock (_stateChangeLock)
            {
                data = _options.Data;
                headers = _options.Headers;
            }

            var connectRequest = new ConnectRequest
            {
                Token = token ?? string.Empty,
                Name = _options.Name,
                Version = _options.Version
            };

            if (!data.IsEmpty)
            {
                connectRequest.Data = ByteString.CopyFrom(data.Span);
            }

            if (headers != null && headers.Count > 0)
            {
                foreach (var kvp in headers)
                {
                    connectRequest.Headers.Add(kvp.Key, kvp.Value);
                }
            }

            // Include server subscriptions for recovery
            foreach (var kvp in _serverSubscriptions)
            {
                var channel = kvp.Key;
                var serverSub = kvp.Value;

                if (serverSub.Recoverable)
                {
                    var subRequest = new Centrifugal.Centrifuge.Protocol.SubscribeRequest
                    {
                        Channel = channel,
                        Recover = true,
                        Offset = serverSub.Offset,
                        Epoch = serverSub.Epoch
                    };
                    connectRequest.Subs.Add(channel, subRequest);
                }
            }

            var cmd = new Command
            {
                Id = NextCommandId(),
                Connect = connectRequest
            };

            return cmd;
        }

        private async Task SendConnectCommandAsync()
        {
            _logger?.LogDebug($"SendConnectCommandAsync - UsesEmulation: {_transport!.UsesEmulation}");
            // For emulation transports, reuse the command that was already sent during OpenAsync
            if (_transport!.UsesEmulation && _pendingConnectCommand != null)
            {
                var pendingCmd = _pendingConnectCommand;
                _pendingConnectCommand = null; // Clear it so it's not reused on reconnect

                // The pending call was already registered before OpenAsync, just wait for reply
                if (!_pendingCalls.TryGetValue(pendingCmd.Id, out var tcs))
                {
                    throw new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "Connect command not registered");
                }

                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(_options.Timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(System.Threading.Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != tcs.Task)
                {
                    _pendingCalls.TryRemove(pendingCmd.Id, out _);
                    throw new CentrifugeTimeoutException();
                }

                var connectReply = await tcs.Task.ConfigureAwait(false);

                if (connectReply.Error != null)
                {
                    var error = new CentrifugeException((int)connectReply.Error.Code, connectReply.Error.Message, connectReply.Error.Temporary);
                    throw error;
                }

                HandleConnectReply(connectReply.Connect);
                return;
            }

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
                    token = await _options.GetToken().ConfigureAwait(false);
                    lock (_stateChangeLock)
                    {
                        _options.Token = token;
                    }
                }
                catch (CentrifugeUnauthorizedException)
                {
                    await SetDisconnectedAsync(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
                    return;
                }
            }

            ReadOnlyMemory<byte> data;
            Dictionary<string, string>? headers;
            lock (_stateChangeLock)
            {
                data = _options.Data;
                headers = _options.Headers;
            }

            var connectRequest = new ConnectRequest
            {
                Token = token ?? string.Empty,
                Name = _options.Name,
                Version = _options.Version
            };

            if (!data.IsEmpty)
            {
                connectRequest.Data = ByteString.CopyFrom(data.Span);
            }

            if (headers != null && headers.Count > 0)
            {
                foreach (var kvp in headers)
                {
                    connectRequest.Headers.Add(kvp.Key, kvp.Value);
                }
            }

            // Include server subscriptions for recovery
            foreach (var kvp in _serverSubscriptions)
            {
                var channel = kvp.Key;
                var serverSub = kvp.Value;

                if (serverSub.Recoverable)
                {
                    var subRequest = new Centrifugal.Centrifuge.Protocol.SubscribeRequest
                    {
                        Channel = channel,
                        Recover = true,
                        Offset = serverSub.Offset,
                        Epoch = serverSub.Epoch
                    };
                    connectRequest.Subs.Add(channel, subRequest);
                }
            }

            var cmd = new Command
            {
                Id = NextCommandId(),
                Connect = connectRequest
            };

            _logger?.LogDebug($"Sending connect command with ID: {cmd.Id}");
            var reply = await SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);
            _logger?.LogDebug($"Received reply for connect command ID: {cmd.Id}");

            if (reply.Error != null)
            {
                var error = new CentrifugeException((int)reply.Error.Code, reply.Error.Message, reply.Error.Temporary);

                // Error code 109 means token expired - mark for refresh on next connect
                if (reply.Error.Code == 109)
                {
                    _refreshRequired = true;
                }

                throw error;
            }

            HandleConnectReply(reply.Connect);
        }

        private void HandleConnectReply(ConnectResult connectResult)
        {
            // Check if this is a reconnection (we had a client ID from a previous connection)
            bool isReconnect = !string.IsNullOrEmpty(_clientId);

            _clientId = connectResult.Client;
            _session = connectResult.Session;
            _node = connectResult.Node;

            SetState(CentrifugeClientState.Connected);

            _transportIsOpen = true;

            Connected?.Invoke(this, new CentrifugeConnectedEventArgs(
                connectResult.Client,
                _transport?.Name ?? "unknown",
                connectResult.Data.ToByteArray()
            ));

            _reconnectAttempts = 0;

            // Clear refresh timer and reset attempts
            ClearRefreshTimer();
            _refreshAttempts = 0;
            _refreshRequired = false;

            // Resolve ready promises
            ResolvePromises();

            // Schedule token refresh if token expires
            if (connectResult.Expires)
            {
                ScheduleConnectionRefresh(connectResult.Ttl);
            }

            // Start ping timer if server sends pings
            if (connectResult.Ping > 0)
            {
                _serverPingInterval = connectResult.Ping;
                _sendPong = connectResult.Pong;
                StartPingTimer(connectResult.Ping);
            }
            else
            {
                _serverPingInterval = 0;
                _sendPong = false;
            }

            // Process server-side subscriptions
            ProcessServerSubscriptions(connectResult.Subs);

            // Send subscribe commands for all subscriptions in Subscribing state
            // This handles both first connect and reconnect scenarios
            SendSubscribeCommands();
        }

        private void SendSubscribeCommands()
        {
            _ = Task.Run(async () =>
            {
                await SendSubscribeCommandsAsync().ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Triggers sending subscribe commands for all subscriptions in Subscribing state.
        /// Commands will be automatically batched by the general command batching mechanism.
        /// </summary>
        internal void ScheduleSubscribeBatch()
        {
            _ = Task.Run(async () =>
            {
                await SendSubscribeCommandsAsync().ConfigureAwait(false);
            });
        }

        private async Task SendSubscribeCommandsAsync()
        {
            // Check client state before processing
            if (_state != CentrifugeClientState.Connected || !_transportIsOpen)
            {
                return;
            }

            // Start all subscribe commands concurrently so they can be batched together
            var subscribeTasks = _subscriptions.Values
                .Where(sub => sub.State == CentrifugeSubscriptionState.Subscribing)
                .Select(sub => sub.SendSubscribeIfNeededAsync())
                .ToList();

            if (subscribeTasks.Any())
            {
                await Task.WhenAll(subscribeTasks).ConfigureAwait(false);
            }
        }

        private void OnTransportMessage(object? sender, byte[] data)
        {
            // Defensive check: don't process events if client is disposed
            if (_disposed) return;

            try
            {
                // Reset ping timer on any message received
                if (_serverPingInterval > 0)
                {
                    ResetPingTimer();
                }

                var reply = Reply.Parser.ParseFrom(data);
                HandleReply(reply);
            }
            catch (Exception ex)
            {
                OnError("parse", ex);
            }
        }

        private void HandleReply(Reply reply)
        {
            if (reply.Id > 0)
            {
                // This is a reply to a command
                if (_pendingCalls.TryRemove(reply.Id, out var tcs))
                {
                    tcs.TrySetResult(reply);
                }
            }
            else if (reply.Push != null)
            {
                // This is a server push
                HandlePush(reply.Push);
            }
            else
            {
                // Empty reply - this is a server ping
                HandleServerPing();
            }
        }

        private void HandlePush(Push push)
        {
            if (push.Pub != null)
            {
                // Dispatch to client-side subscription if exists
                if (_subscriptions.TryGetValue(push.Channel, out var sub))
                {
                    sub.HandlePublication(push.Pub);
                }
                else
                {
                    // Server-side subscription
                    var pub = CreatePublicationArgs(push.Channel, push.Pub);
                    Publication?.Invoke(this, pub);
                }
            }
            else if (push.Join != null)
            {
                if (_subscriptions.TryGetValue(push.Channel, out var sub))
                {
                    sub.HandleJoin(push.Join);
                }
                else
                {
                    // Server-side subscription join
                    if (push.Join.Info != null)
                    {
                        var info = new CentrifugeClientInfo(
                            push.Join.Info.User,
                            push.Join.Info.Client,
                            push.Join.Info.ConnInfo.ToByteArray(),
                            push.Join.Info.ChanInfo.ToByteArray()
                        );
                        Join?.Invoke(this, new CentrifugeJoinEventArgs(push.Channel, info));
                    }
                }
            }
            else if (push.Leave != null)
            {
                if (_subscriptions.TryGetValue(push.Channel, out var sub))
                {
                    sub.HandleLeave(push.Leave);
                }
                else
                {
                    // Server-side subscription leave
                    if (push.Leave.Info != null)
                    {
                        var info = new CentrifugeClientInfo(
                            push.Leave.Info.User,
                            push.Leave.Info.Client,
                            push.Leave.Info.ConnInfo.ToByteArray(),
                            push.Leave.Info.ChanInfo.ToByteArray()
                        );
                        Leave?.Invoke(this, new CentrifugeLeaveEventArgs(push.Channel, info));
                    }
                }
            }
            else if (push.Message != null)
            {
                Message?.Invoke(this, new CentrifugeMessageEventArgs(push.Message.Data.ToByteArray()));
            }
            else if (push.Disconnect != null)
            {
                _logger?.LogDebug($"Received Disconnect push - code: {push.Disconnect.Code}, reason: '{push.Disconnect.Reason}'");
                HandleDisconnectPush(push.Disconnect);
            }
            else if (push.Subscribe != null)
            {
                HandleServerSubscribe(push.Channel, push.Subscribe);
            }
            else if (push.Unsubscribe != null)
            {
                HandleServerUnsubscribe(push.Channel, push.Unsubscribe);
            }
        }

        private void HandleDisconnectPush(Disconnect disconnect)
        {
            int code = (int)disconnect.Code;
            bool reconnect = true;
            _logger?.LogDebug($"HandleDisconnectPush - code: {code}, reason: '{disconnect.Reason}'");

            // Check if this is a non-reconnectable disconnect code
            // Codes 3500-3999 and 4500-4999 mean permanent disconnect
            if ((code >= 3500 && code < 4000) || (code >= 4500 && code < 5000))
            {
                reconnect = false;
            }

            if (reconnect)
            {
                // Disconnect with reconnect
                _logger?.LogDebug($"HandleDisconnectPush - calling HandleTransportClosedAsync for reconnect");
                _ = HandleTransportClosedAsync(new TransportClosedEventArgs(code: code, reason: disconnect.Reason));
            }
            else
            {
                // Permanent disconnect
                _logger?.LogDebug($"HandleDisconnectPush - calling SetDisconnectedAsync for permanent disconnect");
                _ = SetDisconnectedAsync(code, disconnect.Reason, false);
            }
        }

        private void OnTransportClosed(object? sender, TransportClosedEventArgs e)
        {
            // Defensive check: don't process events if client is disposed
            if (_disposed) return;

            _logger?.LogDebug($"OnTransportClosed - code: {e.Code}, reason: '{e.Reason}'");
            _ = HandleTransportClosedAsync(e);
        }

        private async Task HandleTransportClosedAsync(TransportClosedEventArgs e)
        {
            _logger?.LogDebug($"HandleTransportClosedAsync - state: {_state}, code: {e.Code}, reason: '{e.Reason}'");

            // Don't process if already disconnected
            if (_state == CentrifugeClientState.Disconnected)
            {
                _logger?.LogDebug($"HandleTransportClosedAsync - skipping (state is {_state})");
                return;
            }

            // If already in Connecting state, cancel any pending reconnect and restart
            // This handles the case where transport closes during connection (e.g., server sends disconnect while connecting)
            if (_state == CentrifugeClientState.Connecting)
            {
                _logger?.LogDebug($"HandleTransportClosedAsync - transport closed while connecting, restarting reconnect logic");
                _reconnectCts?.Cancel();
            }

            // Determine if we should reconnect based on close code
            bool shouldReconnect = true;
            int code = e.Code ?? 0;
            string reason = e.Reason;
            _logger?.LogDebug($"HandleTransportClosedAsync - processing with code: {code}, reason: '{reason}'");

            // Check for non-reconnectable disconnect codes
            // Codes 3500-3999 and 4500-4999 mean permanent disconnect
            // Also BadProtocol, Unauthorized, and MessageSizeLimit are permanent
            if (e.Code.HasValue)
            {
                if ((code >= 3500 && code < 4000) || (code >= 4500 && code < 5000) ||
                    code == CentrifugeDisconnectedCodes.BadProtocol ||
                    code == CentrifugeDisconnectedCodes.Unauthorized ||
                    code == CentrifugeDisconnectedCodes.MessageSizeLimit)
                {
                    shouldReconnect = false;
                }
            }

            // Check for specific exceptions that indicate permanent failure
            if (e.Exception != null && !e.Code.HasValue)
            {
                if (e.Exception is CentrifugeUnauthorizedException)
                {
                    shouldReconnect = false;
                    code = CentrifugeDisconnectedCodes.Unauthorized;
                    reason = "unauthorized";
                }
            }

            if (!shouldReconnect)
            {
                // Permanent disconnect
                await SetDisconnectedAsync(code, reason, false).ConfigureAwait(false);
                return;
            }

            // If using multi-transport fallback and transport closed before opening,
            // try the next transport in the list
            if (_transportEndpoints != null && !_transportWasOpen)
            {
                _currentTransportIndex++;
                if (_currentTransportIndex >= _transportEndpoints.Count)
                {
                    _currentTransportIndex = 0;
                }
            }

            // Set state to Connecting FIRST to prevent duplicate processing
            // This must happen before CleanupTransportAsync to block concurrent HandleTransportClosedAsync calls
            SetState(CentrifugeClientState.Connecting);

            // Move all subscribed subscriptions to subscribing state BEFORE emitting connecting event
            foreach (var sub in _subscriptions.Values)
            {
                sub.MoveToSubscribing(CentrifugeSubscribingCodes.TransportClosed, "transport closed");
            }

            // Use the actual disconnect code and reason from the server (not hardcoded TransportClosed)
            _logger?.LogDebug($"Firing Connecting event with code: {code}, reason: '{reason}'");
            Connecting?.Invoke(this, new CentrifugeConnectingEventArgs(code, reason));

            // Clean up the old transport AFTER firing events
            // By this point, state is Connecting, so any WebSocket close events will be ignored
            await CleanupTransportAsync().ConfigureAwait(false);

            await ScheduleReconnectAsync().ConfigureAwait(false);
        }

        private void OnTransportError(object? sender, Exception e)
        {
            // Defensive check: don't process events if client is disposed
            if (_disposed) return;

            OnError("transport", e);
        }

        internal async Task HandleSubscribeTimeoutAsync()
        {
            // Subscribe timeout triggers client disconnect with reconnect
            if (_state == CentrifugeClientState.Disconnected)
            {
                return;
            }

            await StartConnectingAsync(CentrifugeConnectingCodes.SubscribeTimeout, "subscribe timeout").ConfigureAwait(false);

            // Properly clean up transport before reconnecting
            await CleanupTransportAsync().ConfigureAwait(false);

            await ScheduleReconnectAsync().ConfigureAwait(false);
        }

        internal async Task HandleUnsubscribeErrorAsync()
        {
            // Unsubscribe error triggers client disconnect with reconnect (matching centrifuge-js behavior)
            if (_state == CentrifugeClientState.Disconnected)
            {
                return;
            }

            await StartConnectingAsync(CentrifugeConnectingCodes.UnsubscribeError, "unsubscribe error").ConfigureAwait(false);

            // Properly clean up transport before reconnecting
            await CleanupTransportAsync().ConfigureAwait(false);

            await ScheduleReconnectAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Cleans up the current transport (unsubscribes events, closes, disposes).
        /// This should be called before reconnecting to prevent duplicate connections.
        /// </summary>
        private async Task CleanupTransportAsync()
        {
            if (_transport == null)
            {
                return;
            }

            try
            {
                // Stop ping timer
                _pingTimer?.Dispose();
                _pingTimer = null;
                _serverPingInterval = 0;
                _sendPong = false;

                // Clear inflight requests
                ClearInflightRequests();

                // Unsubscribe from transport events BEFORE closing to prevent race conditions
                _transport.Opened -= OnTransportOpened;
                _transport.MessageReceived -= OnTransportMessage;
                _transport.Closed -= OnTransportClosed;
                _transport.Error -= OnTransportError;

                // Close and dispose transport
                await _transport.CloseAsync().ConfigureAwait(false);
                _transport.Dispose();
                _transport = null;
            }
            catch
            {
                // Ignore cleanup errors, but ensure transport is set to null
                _transport = null;
            }
        }

        private async Task ScheduleReconnectAsync()
        {
            if (_state == CentrifugeClientState.Disconnected)
            {
                return;
            }

            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();

            int delay = Utilities.CalculateBackoff(
                _reconnectAttempts++,
                _options.MinReconnectDelay,
                _options.MaxReconnectDelay
            );

            try
            {
                await Task.Delay(delay, _reconnectCts.Token).ConfigureAwait(false);
                await CreateTransportAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Reconnect was cancelled
            }
            catch (Exception ex)
            {
                // CreateTransportAsync failed - report error and schedule next reconnection attempt
                // If using multi-transport fallback and transport failed before opening,
                // try the next transport in the list
                if (_transportEndpoints != null && !_transportWasOpen)
                {
                    _currentTransportIndex++;
                    if (_currentTransportIndex >= _transportEndpoints.Count)
                    {
                        _currentTransportIndex = 0;
                    }
                }

                OnError("transport", ex);

                // Schedule next reconnection attempt
                await ScheduleReconnectAsync().ConfigureAwait(false);
            }
        }

        private async Task SetDisconnectedAsync(int code, string reason, bool shouldReconnect)
        {
            // Change state synchronously first
            lock (_stateChangeLock)
            {
                if (_state == CentrifugeClientState.Disconnected)
                {
                    return;
                }

                if (shouldReconnect)
                {
                    SetState(CentrifugeClientState.Connecting);
                    Connecting?.Invoke(this, new CentrifugeConnectingEventArgs(code, reason));
                }
                else
                {
                    SetState(CentrifugeClientState.Disconnected);
                    Disconnected?.Invoke(this, new CentrifugeDisconnectedEventArgs(code, reason));

                    // Reject ready promises when disconnecting
                    RejectPromises(new CentrifugeException(CentrifugeErrorCodes.ClientDisconnected, "client disconnected"));
                }

                _transportIsOpen = false;
            }

            // Now do async cleanup without holding locks
            // Defense-in-depth: catch ObjectDisposedException in case an event handler was already
            // executing when we unsubscribed, or if there's a race with disposal
            try
            {
                await _stateLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Semaphore was disposed - client is shutting down, skip cleanup
                return;
            }

            try
            {
                _reconnectCts?.Cancel();
                ClearRefreshTimer();
                _transportWasOpen = false;

                // Clean up transport (ping timer, events, close, dispose)
                await CleanupTransportAsync().ConfigureAwait(false);

                // Unsubscribe all subscriptions
                foreach (var sub in _subscriptions.Values)
                {
                    _ = sub.SetUnsubscribedAsync(CentrifugeUnsubscribedCodes.ClientClosed, "client closed");
                }
            }
            finally
            {
                try
                {
                    _stateLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed during cleanup - ignore
                }
            }
        }

        private void SetState(CentrifugeClientState newState)
        {
            var oldState = _state;
            _state = newState;

            if (oldState != newState)
            {
                StateChanged?.Invoke(this, new CentrifugeStateEventArgs(oldState, newState));
            }
        }

        private void StartPingTimer(uint pingInterval)
        {
            _pingTimer?.Dispose();

            var interval = (int)(pingInterval * 1000) + (int)_options.MaxServerPingDelay.TotalMilliseconds;
            _pingTimer = new Timer(_ =>
            {
                _ = StartConnectingAsync(CentrifugeConnectingCodes.NoPing, "no ping");
            }, null, interval, System.Threading.Timeout.Infinite);
        }

        private void ResetPingTimer()
        {
            if (_pingTimer == null || _serverPingInterval == 0 || _state != CentrifugeClientState.Connected)
            {
                return;
            }

            var interval = (int)(_serverPingInterval * 1000) + (int)_options.MaxServerPingDelay.TotalMilliseconds;
            _pingTimer.Change(interval, System.Threading.Timeout.Infinite);
        }

        private void HandleServerPing()
        {
            if (_sendPong)
            {
                // Send empty command as pong
                var cmd = new Command
                {
                    // No Id, no payload - just empty command
                };

                try
                {
                    if (_transport != null)
                    {
                        if (_transport.UsesEmulation)
                        {
                            // For emulation transports (non-connect commands), use SendEmulationAsync with session and node
                            // The command must be varint-delimited
                            using var ms = new MemoryStream();
                            VarintCodec.WriteDelimitedMessage(ms, cmd.ToByteArray());
                            var delimitedCommand = ms.ToArray();

                            var emulationEndpoint = GetEmulationEndpoint();
                            _transport.SendEmulationAsync(delimitedCommand, _session, _node, emulationEndpoint, CancellationToken.None);
                        }
                        else
                        {
                            // For WebSocket, wrap with varint delimiter
                            using var ms = new MemoryStream();
                            VarintCodec.WriteDelimitedMessage(ms, cmd.ToByteArray());
                            _transport.SendAsync(ms.ToArray()).Wait();
                        }
                    }
                }
                catch
                {
                    // Ignore errors sending pong
                }
            }
        }

        private void ScheduleConnectionRefresh(uint ttl)
        {
            _refreshTimer?.Dispose();
            var delay = Utilities.TtlToMilliseconds(ttl);
            _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, System.Threading.Timeout.Infinite);
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        private async Task RefreshConnectionTokenAsync()
        {
            if (_state != CentrifugeClientState.Connected || _options.GetToken == null)
            {
                return;
            }

            try
            {
                var token = await _options.GetToken().ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                {
                    await SetDisconnectedAsync(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
                    return;
                }

                lock (_stateChangeLock)
                {
                    _options.Token = token;
                }

                // Send refresh request to server
                var cmd = new Command
                {
                    Id = NextCommandId(),
                    Refresh = new RefreshRequest
                    {
                        Token = token
                    }
                };

                var reply = await SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);

                if (reply.Error != null)
                {
                    HandleRefreshError(new CentrifugeException(
                        (int)reply.Error.Code,
                        reply.Error.Message,
                        reply.Error.Temporary
                    ));
                    return;
                }

                HandleRefreshReply(reply.Refresh);
            }
            catch (CentrifugeUnauthorizedException)
            {
                await SetDisconnectedAsync(CentrifugeDisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError("refreshToken", ex);
                // Schedule retry
                var delay = Utilities.CalculateBackoff(_refreshAttempts, _options.MinReconnectDelay, _options.MaxReconnectDelay);
                _refreshAttempts++;
                _refreshTimer?.Dispose();
                _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, System.Threading.Timeout.Infinite);
            }
        }

        private void HandleRefreshReply(RefreshResult result)
        {
            if (_state != CentrifugeClientState.Connected)
            {
                return;
            }

            ClearRefreshTimer();
            _refreshAttempts = 0;
            _clientId = result.Client;

            // Schedule next refresh if token expires
            if (result.Expires)
            {
                ScheduleConnectionRefresh(result.Ttl);
            }
        }

        private void HandleRefreshError(CentrifugeException error)
        {
            // If error is temporary, retry with backoff
            if (error.Code < 100 || error.Temporary)
            {
                OnError("refreshToken", error);
                var delay = Utilities.CalculateBackoff(_refreshAttempts, _options.MinReconnectDelay, _options.MaxReconnectDelay);
                _refreshAttempts++;
                _refreshTimer?.Dispose();
                _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, System.Threading.Timeout.Infinite);
            }
            else
            {
                // Permanent error - disconnect
                _ = SetDisconnectedAsync((int)error.Code, error.Message, false);
            }
        }

        private void ClearInflightRequests()
        {
            // Cancel all pending requests with connection closed error
            foreach (var kvp in _pendingCalls)
            {
                kvp.Value.TrySetException(new CentrifugeException(
                    CentrifugeErrorCodes.ConnectionClosed,
                    "connection closed",
                    false
                ));
            }
            _pendingCalls.Clear();
        }

        private void ProcessServerSubscriptions(Google.Protobuf.Collections.MapField<string, SubscribeResult> subs)
        {
            if (subs == null || subs.Count == 0)
            {
                return;
            }

            var subsToKeep = new HashSet<string>();

            // Process new/updated subscriptions
            foreach (var kvp in subs)
            {
                var channel = kvp.Key;
                var sub = kvp.Value;
                subsToKeep.Add(channel);

                bool wasRecovering = _serverSubscriptions.ContainsKey(channel);

                // Fire ServerSubscribing event for new subscriptions
                if (!wasRecovering)
                {
                    ServerSubscribing?.Invoke(this, new CentrifugeServerSubscribingEventArgs(channel));
                }

                _serverSubscriptions[channel] = new ServerSubscription
                {
                    Offset = sub.Offset,
                    Epoch = sub.Epoch,
                    Recoverable = sub.Recoverable
                };

                CentrifugeStreamPosition? streamPosition = null;
                if (sub.Positioned)
                {
                    streamPosition = new CentrifugeStreamPosition(sub.Offset, sub.Epoch);
                }

                ServerSubscribed?.Invoke(this, new CentrifugeServerSubscribedEventArgs(
                    channel,
                    wasRecovering,
                    sub.Recovered,
                    sub.Recoverable,
                    sub.Positioned,
                    streamPosition,
                    sub.Data.ToByteArray()
                ));

                // Dispatch recovered publications
                if (sub.Recovered)
                {
                    foreach (var pub in sub.Publications)
                    {
                        var pubArgs = CreatePublicationArgs(channel, pub);
                        Publication?.Invoke(this, pubArgs);

                        if (sub.Positioned && pub.Offset > 0)
                        {
                            _serverSubscriptions[channel].Offset = pub.Offset;
                        }
                    }
                }
            }

            // Check for removed subscriptions
            var channelsToRemove = new List<string>();
            foreach (var channel in _serverSubscriptions.Keys)
            {
                if (!subsToKeep.Contains(channel))
                {
                    channelsToRemove.Add(channel);
                }
            }

            foreach (var channel in channelsToRemove)
            {
                _serverSubscriptions.TryRemove(channel, out _);
                ServerUnsubscribed?.Invoke(this, new CentrifugeServerUnsubscribedEventArgs(channel));
            }
        }

        private void HandleServerSubscribe(string channel, Subscribe sub)
        {
            bool wasRecovering = _serverSubscriptions.ContainsKey(channel);

            _serverSubscriptions[channel] = new ServerSubscription
            {
                Offset = sub.Offset,
                Epoch = sub.Epoch,
                Recoverable = sub.Recoverable
            };

            CentrifugeStreamPosition? streamPosition = null;
            if (sub.Positioned)
            {
                streamPosition = new CentrifugeStreamPosition(sub.Offset, sub.Epoch);
            }

            // Fire ServerSubscribing event for new subscriptions
            if (!wasRecovering)
            {
                ServerSubscribing?.Invoke(this, new CentrifugeServerSubscribingEventArgs(channel));
            }

            ServerSubscribed?.Invoke(this, new CentrifugeServerSubscribedEventArgs(
                channel,
                wasRecovering,
                false, // Subscribe push doesn't include recovered flag
                sub.Recoverable,
                sub.Positioned,
                streamPosition,
                sub.Data.ToByteArray()
            ));
        }

        private void HandleServerUnsubscribe(string channel, Unsubscribe unsubscribe)
        {
            // Check if this is a client-side subscription
            if (_subscriptions.TryGetValue(channel, out var clientSub))
            {
                if (unsubscribe.Code < 2500)
                {
                    // Permanent unsubscribe
                    _ = clientSub.SetUnsubscribedAsync((int)unsubscribe.Code, unsubscribe.Reason);
                }
                else
                {
                    // Temporary unsubscribe - resubscribe
                    _ = clientSub.ResubscribeAsync();
                }
            }
            else if (_serverSubscriptions.ContainsKey(channel))
            {
                // Server-side subscription
                _serverSubscriptions.TryRemove(channel, out _);
                ServerUnsubscribed?.Invoke(this, new CentrifugeServerUnsubscribedEventArgs(channel));
            }
        }

        private void OnError(string type, Exception exception)
        {
            Error?.Invoke(this, new CentrifugeErrorEventArgs(type, 0, exception.Message, false, exception));
        }

        internal uint NextCommandId()
        {
            return (uint)Interlocked.Increment(ref _commandId);
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

        internal static CentrifugePublicationEventArgs CreatePublicationArgs(string channel, Publication pub)
        {
            return CreatePublicationArgs(channel, pub, pub.Data.ToByteArray());
        }

        internal static CentrifugePublicationEventArgs CreatePublicationArgs(string channel, Publication pub, byte[] data)
        {
            CentrifugeClientInfo? info = null;
            if (pub.Info != null)
            {
                info = new CentrifugeClientInfo(
                    pub.Info.User,
                    pub.Info.Client,
                    pub.Info.ConnInfo.ToByteArray(),
                    pub.Info.ChanInfo.ToByteArray()
                );
            }

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

        /// <summary>
        /// Asynchronously disposes the client, ensuring disconnect completes before releasing resources.
        /// This is the recommended way to dispose the client.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await SetDisconnectedAsync(CentrifugeDisconnectedCodes.DisconnectCalled, "disconnect called", false).ConfigureAwait(false);
            }
            catch
            {
                // Suppress exceptions during disposal - we're shutting down anyway
            }

            _stateLock?.Dispose();
            _reconnectCts?.Dispose();
            _pingTimer?.Dispose();
            _refreshTimer?.Dispose();
            _commandBatchTimer?.Dispose();
        }

        /// <summary>
        /// Synchronously disposes the client. This blocks until disconnect completes.
        /// Consider using DisposeAsync() instead for better async/await support.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                SetDisconnectedAsync(CentrifugeDisconnectedCodes.DisconnectCalled, "disconnect called", false)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // Suppress exceptions during disposal - we're shutting down anyway
            }

            _stateLock?.Dispose();
            _reconnectCts?.Dispose();
            _pingTimer?.Dispose();
            _refreshTimer?.Dispose();
            _commandBatchTimer?.Dispose();
        }
    }
}
