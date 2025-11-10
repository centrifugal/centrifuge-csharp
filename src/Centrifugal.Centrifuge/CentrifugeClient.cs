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
    public class CentrifugeClient : IDisposable
    {
        private readonly string? _endpoint;
        private readonly List<TransportEndpoint>? _transportEndpoints;
        private readonly CentrifugeClientOptions _options;
        private readonly ConcurrentDictionary<string, CentrifugeSubscription> _subscriptions = new();
        private readonly ConcurrentDictionary<string, ServerSubscription> _serverSubscriptions = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Reply>> _pendingCalls = new();
        private readonly SemaphoreSlim _stateLock = new SemaphoreSlim(1, 1);
        private readonly object _stateChangeLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _readyPromises = new();

        private ITransport? _transport;
        private ClientState _state = ClientState.Disconnected;
        private int _commandId;
        private int _reconnectAttempts;
        private CancellationTokenSource? _reconnectCts;
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

        /// <summary>
        /// Gets the current client state.
        /// </summary>
        public ClientState State => _state;

        /// <summary>
        /// Gets whether the transport is currently open.
        /// </summary>
        internal bool TransportIsOpen => _transportIsOpen;

        /// <summary>
        /// Event raised when client state changes.
        /// </summary>
        public event EventHandler<StateEventArgs>? StateChanged;

        /// <summary>
        /// Event raised when client is connecting.
        /// </summary>
        public event EventHandler<ConnectingEventArgs>? Connecting;

        /// <summary>
        /// Event raised when client is connected.
        /// </summary>
        public event EventHandler<ConnectedEventArgs>? Connected;

        /// <summary>
        /// Event raised when client is disconnected.
        /// </summary>
        public event EventHandler<DisconnectedEventArgs>? Disconnected;

        /// <summary>
        /// Event raised when an error occurs.
        /// </summary>
        public event EventHandler<ErrorEventArgs>? Error;

        /// <summary>
        /// Event raised when a message is received from server.
        /// </summary>
        public event EventHandler<MessageEventArgs>? Message;

        /// <summary>
        /// Event raised for server-side subscription publications.
        /// </summary>
        public event EventHandler<PublicationEventArgs>? Publication;

        /// <summary>
        /// Event raised for server-side subscription join events.
        /// </summary>
        public event EventHandler<JoinEventArgs>? Join;

        /// <summary>
        /// Event raised for server-side subscription leave events.
        /// </summary>
        public event EventHandler<LeaveEventArgs>? Leave;

        /// <summary>
        /// Event raised when server-side subscription is subscribing.
        /// </summary>
        public event EventHandler<ServerSubscribingEventArgs>? ServerSubscribing;

        /// <summary>
        /// Event raised when server-side subscription is subscribed.
        /// </summary>
        public event EventHandler<ServerSubscribedEventArgs>? ServerSubscribed;

        /// <summary>
        /// Event raised when server-side subscription is unsubscribed.
        /// </summary>
        public event EventHandler<ServerUnsubscribedEventArgs>? ServerUnsubscribed;

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
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeClient"/> class with multi-transport fallback.
        /// </summary>
        /// <param name="transportEndpoints">Array of transport endpoints to try in order.</param>
        /// <param name="options">Client options.</param>
        public CentrifugeClient(TransportEndpoint[] transportEndpoints, CentrifugeClientOptions? options = null)
        {
            if (transportEndpoints == null || transportEndpoints.Length == 0)
            {
                throw new ArgumentException("Transport endpoints cannot be null or empty", nameof(transportEndpoints));
            }

            _transportEndpoints = new List<TransportEndpoint>(transportEndpoints);
            _options = options ?? new CentrifugeClientOptions();
            _options.Validate();
        }

        /// <summary>
        /// Connects to the Centrifugo server. This method returns immediately and starts the connection process in the background.
        /// Use ReadyAsync() to wait for the connection to be established, or use the Connected event.
        /// </summary>
        public void Connect()
        {
            if (_state == ClientState.Connected)
            {
                return;
            }

            if (_state == ClientState.Connecting)
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
            _ = SetDisconnectedAsync(DisconnectedCodes.DisconnectCalled, "disconnect called", false);
        }

        /// <summary>
        /// Returns a Task that completes when the client is connected.
        /// If the client is already connected, the Task completes immediately.
        /// If the client is disconnected, the Task is rejected.
        /// </summary>
        /// <param name="timeout">Optional timeout.</param>
        /// <returns>A task that completes when connected.</returns>
        public Task ReadyAsync(TimeSpan? timeout = null)
        {
            switch (_state)
            {
                case ClientState.Disconnected:
                    return Task.FromException(new CentrifugeException(ErrorCodes.ClientDisconnected, "client disconnected"));

                case ClientState.Connected:
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
                                promise.TrySetException(new CentrifugeException(ErrorCodes.Timeout, "timeout"));
                            }
                        });
                    }

                    return tcs.Task;
            }
        }


        /// <summary>
        /// Creates a new subscription to a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="options">Subscription options.</param>
        /// <returns>The subscription instance.</returns>
        public CentrifugeSubscription NewSubscription(string channel, SubscriptionOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentException("Channel cannot be null or empty", nameof(channel));
            }

            if (_subscriptions.ContainsKey(channel))
            {
                throw new InvalidOperationException($"Subscription to channel '{channel}' already exists");
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

            if (subscription.State != SubscriptionState.Unsubscribed)
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
        public void SetData(byte[]? data)
        {
            lock (_stateChangeLock)
            {
                _options.Data = data != null ? (byte[])data.Clone() : null;
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
        /// </summary>
        /// <param name="method">RPC method name.</param>
        /// <param name="data">Request data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>RPC result.</returns>
        public async Task<RpcResult> RpcAsync(string method, byte[] data, CancellationToken cancellationToken = default)
        {
            var cmd = new Command
            {
                Id = NextCommandId(),
                Rpc = new RPCRequest
                {
                    Method = method,
                    Data = ByteString.CopyFrom(data)
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

            return new RpcResult(reply.Rpc?.Data.ToByteArray() ?? Array.Empty<byte>());
        }

        /// <summary>
        /// Sends an asynchronous message to the server (no response expected).
        /// </summary>
        /// <param name="data">Message data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            var cmd = new Command
            {
                Send = new SendRequest
                {
                    Data = ByteString.CopyFrom(data)
                }
            };

            if (_transport == null || _state != ClientState.Connected)
            {
                throw new CentrifugeException(ErrorCodes.ClientDisconnected, "Client is not connected");
            }

            await _transport.SendAsync(cmd.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets presence information for a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence result.</returns>
        public async Task<PresenceResult> PresenceAsync(string channel, CancellationToken cancellationToken = default)
        {
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

            var clients = new Dictionary<string, ClientInfo>();
            foreach (var kvp in reply.Presence.Presence)
            {
                var info = kvp.Value;
                clients[kvp.Key] = new ClientInfo(
                    info.User,
                    info.Client,
                    info.ConnInfo.ToByteArray(),
                    info.ChanInfo.ToByteArray()
                );
            }

            return new PresenceResult(clients);
        }

        /// <summary>
        /// Gets presence stats for a channel.
        /// </summary>
        /// <param name="channel">Channel name.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Presence stats result.</returns>
        public async Task<PresenceStatsResult> PresenceStatsAsync(string channel, CancellationToken cancellationToken = default)
        {
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

            return new PresenceStatsResult(
                reply.PresenceStats.NumClients,
                reply.PresenceStats.NumUsers
            );
        }

        internal async Task<Reply> SendCommandAsync(Command command, CancellationToken cancellationToken)
        {
            if (_transport == null || (_state != ClientState.Connected && _state != ClientState.Connecting))
            {
                throw new CentrifugeException(ErrorCodes.ClientDisconnected, "Client is not connected");
            }

            var tcs = new TaskCompletionSource<Reply>();
            _pendingCalls[command.Id] = tcs;

            try
            {
                // Don't send connect command for emulation transports - it was already sent during OpenAsync
                bool isConnectCommand = command.Connect != null;
                if (!isConnectCommand || !_transport.UsesEmulation)
                {
                    if (_transport.UsesEmulation)
                    {
                        // For emulation transports (non-connect commands), use SendEmulationAsync with session and node
                        // The command must be varint-delimited
                        using var ms = new MemoryStream();
                        VarintCodec.WriteDelimitedMessage(ms, command.ToByteArray());
                        var delimitedCommand = ms.ToArray();

                        var emulationEndpoint = GetEmulationEndpoint();
                        await _transport.SendEmulationAsync(delimitedCommand, _session, _node, emulationEndpoint, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // For WebSocket, use regular SendAsync
                        await _transport.SendAsync(command.ToByteArray(), cancellationToken).ConfigureAwait(false);
                    }
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.Timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != tcs.Task)
                {
                    throw new TimeoutException();
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pendingCalls.TryRemove(command.Id, out _);
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
            string endpoint = _endpoint ?? _transportEndpoints?[_currentTransportIndex].Endpoint ?? throw new ConfigurationException("No endpoint configured");

            var uri = new Uri(endpoint);
            return $"{uri.Scheme}://{uri.Authority}/emulation";
        }

        private void StartConnecting()
        {
            SetState(ClientState.Connecting);
            Connecting?.Invoke(this, new ConnectingEventArgs(ConnectingCodes.ConnectCalled, "connect called"));

            _ = Task.Run(async () =>
            {
                try
                {
                    await CreateTransportAsync().ConfigureAwait(false);
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
            SetState(ClientState.Connecting);
            Connecting?.Invoke(this, new ConnectingEventArgs(code, reason));

            try
            {
                await CreateTransportAsync().ConfigureAwait(false);
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
            _transport?.Dispose();

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
                    catch (ConfigurationException)
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
                    throw new ConfigurationException("No supported transport found in the transport endpoints list");
                }
            }
            // Single endpoint mode (legacy)
            else if (_endpoint != null)
            {
                // Determine transport type from endpoint
                if (_endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                    _endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    transport = new WebSocketTransport(_endpoint);
                    _transport = transport;
                }
                else
                {
                    throw new ConfigurationException("Only WebSocket endpoints are supported in this version");
                }
            }
            else
            {
                throw new ConfigurationException("No endpoint configured");
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

        private ITransport CreateTransport(TransportType transportType, string endpoint)
        {
            switch (transportType)
            {
                case TransportType.WebSocket:
                    return new WebSocketTransport(endpoint);
                case TransportType.SSE:
                    throw new ConfigurationException("SSE transport is not currently supported in this client. SSE requires JSON protocol which is not yet implemented. Use WebSocket or HttpStream instead.");
                case TransportType.HttpStream:
                    return new HttpStreamTransport(endpoint);
                default:
                    throw new ConfigurationException($"Unsupported transport type: {transportType}");
            }
        }

        private async void OnTransportOpened(object? sender, EventArgs e)
        {
            // Mark that at least one transport successfully opened
            _transportWasOpen = true;

            try
            {
                await SendConnectCommandAsync().ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Connect timeout should trigger reconnect, not permanent disconnect
                OnError("connect", new CentrifugeException(ErrorCodes.Timeout, "connect timeout", true));
                _transport?.Dispose();
                _transport = null;
                await ScheduleReconnectAsync().ConfigureAwait(false);
            }
            catch (CentrifugeException ex)
            {
                // Error code 109 (token expired) or temporary errors should trigger reconnect
                if (ex.Code == 109 || ex.Code < 100 || ex.Temporary)
                {
                    OnError("connect", ex);
                    _transport?.Dispose();
                    _transport = null;
                    await ScheduleReconnectAsync().ConfigureAwait(false);
                }
                else
                {
                    // Permanent error - disconnect
                    OnError("connect", ex);
                    await SetDisconnectedAsync(ex.Code, ex.Message, false).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                OnError("connect", ex);
                await SetDisconnectedAsync(DisconnectedCodes.BadProtocol, ex.Message, false).ConfigureAwait(false);
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
                catch (UnauthorizedException)
                {
                    throw;
                }
            }

            byte[]? data;
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

            if (data != null)
            {
                connectRequest.Data = ByteString.CopyFrom(data);
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
            // For emulation transports, reuse the command that was already sent during OpenAsync
            if (_transport!.UsesEmulation && _pendingConnectCommand != null)
            {
                var pendingCmd = _pendingConnectCommand;
                _pendingConnectCommand = null; // Clear it so it's not reused on reconnect

                // The pending call was already registered before OpenAsync, just wait for reply
                if (!_pendingCalls.TryGetValue(pendingCmd.Id, out var tcs))
                {
                    throw new CentrifugeException(ErrorCodes.ClientDisconnected, "Connect command not registered");
                }

                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(_options.Timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != tcs.Task)
                {
                    _pendingCalls.TryRemove(pendingCmd.Id, out _);
                    throw new TimeoutException();
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
                catch (UnauthorizedException)
                {
                    await SetDisconnectedAsync(DisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
                    return;
                }
            }

            byte[]? data;
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

            if (data != null)
            {
                connectRequest.Data = ByteString.CopyFrom(data);
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

            var reply = await SendCommandAsync(cmd, CancellationToken.None).ConfigureAwait(false);

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

            SetState(ClientState.Connected);

            _transportIsOpen = true;

            Connected?.Invoke(this, new ConnectedEventArgs(
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
                foreach (var sub in _subscriptions.Values)
                {
                    if (sub.State == SubscriptionState.Subscribing)
                    {
                        await sub.SendSubscribeIfNeededAsync().ConfigureAwait(false);
                    }
                }
            });
        }

        private void OnTransportMessage(object? sender, byte[] data)
        {
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
                        var info = new ClientInfo(
                            push.Join.Info.User,
                            push.Join.Info.Client,
                            push.Join.Info.ConnInfo.ToByteArray(),
                            push.Join.Info.ChanInfo.ToByteArray()
                        );
                        Join?.Invoke(this, new JoinEventArgs(push.Channel, info));
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
                        var info = new ClientInfo(
                            push.Leave.Info.User,
                            push.Leave.Info.Client,
                            push.Leave.Info.ConnInfo.ToByteArray(),
                            push.Leave.Info.ChanInfo.ToByteArray()
                        );
                        Leave?.Invoke(this, new LeaveEventArgs(push.Channel, info));
                    }
                }
            }
            else if (push.Message != null)
            {
                Message?.Invoke(this, new MessageEventArgs(push.Message.Data.ToByteArray()));
            }
            else if (push.Disconnect != null)
            {
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

            // Check if this is a non-reconnectable disconnect code
            // Codes 3500-3999 and 4500-4999 mean permanent disconnect
            if ((code >= 3500 && code < 4000) || (code >= 4500 && code < 5000))
            {
                reconnect = false;
            }

            if (reconnect)
            {
                // Disconnect with reconnect
                _ = HandleTransportClosedAsync(new TransportClosedEventArgs(code: code, reason: disconnect.Reason));
            }
            else
            {
                // Permanent disconnect
                _ = SetDisconnectedAsync(code, disconnect.Reason, false);
            }
        }

        private void OnTransportClosed(object? sender, TransportClosedEventArgs e)
        {
            _ = HandleTransportClosedAsync(e);
        }

        private async Task HandleTransportClosedAsync(TransportClosedEventArgs e)
        {
            if (_state == ClientState.Disconnected)
            {
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

            await StartConnectingAsync(ConnectingCodes.TransportClosed, e.Reason).ConfigureAwait(false);
            await ScheduleReconnectAsync().ConfigureAwait(false);
        }

        private void OnTransportError(object? sender, Exception e)
        {
            OnError("transport", e);
        }

        internal async Task HandleSubscribeTimeoutAsync()
        {
            // Subscribe timeout triggers client disconnect with reconnect, matching centrifuge-js behavior
            if (_state == ClientState.Disconnected)
            {
                return;
            }

            await StartConnectingAsync(ConnectingCodes.SubscribeTimeout, "subscribe timeout").ConfigureAwait(false);
            _transport?.Dispose();
            _transport = null;
            await ScheduleReconnectAsync().ConfigureAwait(false);
        }

        private async Task ScheduleReconnectAsync()
        {
            if (_state == ClientState.Disconnected)
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
        }

        private async Task SetDisconnectedAsync(int code, string reason, bool shouldReconnect)
        {
            // Change state synchronously first
            lock (_stateChangeLock)
            {
                if (_state == ClientState.Disconnected)
                {
                    return;
                }

                if (shouldReconnect)
                {
                    SetState(ClientState.Connecting);
                    Connecting?.Invoke(this, new ConnectingEventArgs(code, reason));
                }
                else
                {
                    SetState(ClientState.Disconnected);
                    Disconnected?.Invoke(this, new DisconnectedEventArgs(code, reason));

                    // Reject ready promises when disconnecting
                    RejectPromises(new CentrifugeException(ErrorCodes.ClientDisconnected, "client disconnected"));
                }

                _transportIsOpen = false;
            }

            // Now do async cleanup without holding locks
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _reconnectCts?.Cancel();
                _pingTimer?.Dispose();
                ClearRefreshTimer();
                _serverPingInterval = 0;
                _sendPong = false;
                _transportWasOpen = false;

                // Clear all inflight requests
                ClearInflightRequests();

                if (_transport != null)
                {
                    await _transport.CloseAsync().ConfigureAwait(false);
                    _transport.Dispose();
                    _transport = null;
                }

                // Unsubscribe all subscriptions
                foreach (var sub in _subscriptions.Values)
                {
                    _ = sub.SetUnsubscribedAsync(UnsubscribedCodes.ClientClosed, "client closed");
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void SetState(ClientState newState)
        {
            var oldState = _state;
            _state = newState;

            if (oldState != newState)
            {
                StateChanged?.Invoke(this, new StateEventArgs(oldState, newState));
            }
        }

        private void StartPingTimer(uint pingInterval)
        {
            _pingTimer?.Dispose();

            var interval = (int)(pingInterval * 1000) + (int)_options.MaxServerPingDelay.TotalMilliseconds;
            _pingTimer = new Timer(_ =>
            {
                _ = StartConnectingAsync(ConnectingCodes.NoPing, "no ping");
            }, null, interval, Timeout.Infinite);
        }

        private void ResetPingTimer()
        {
            if (_pingTimer == null || _serverPingInterval == 0 || _state != ClientState.Connected)
            {
                return;
            }

            var interval = (int)(_serverPingInterval * 1000) + (int)_options.MaxServerPingDelay.TotalMilliseconds;
            _pingTimer.Change(interval, Timeout.Infinite);
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
                        _transport.SendAsync(cmd.ToByteArray()).Wait();
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
            _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, Timeout.Infinite);
        }

        private void ClearRefreshTimer()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        private async Task RefreshConnectionTokenAsync()
        {
            if (_state != ClientState.Connected || _options.GetToken == null)
            {
                return;
            }

            try
            {
                var token = await _options.GetToken().ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                {
                    await SetDisconnectedAsync(DisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
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
            catch (UnauthorizedException)
            {
                await SetDisconnectedAsync(DisconnectedCodes.Unauthorized, "unauthorized", false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError("refreshToken", ex);
                // Schedule retry
                var delay = Utilities.CalculateBackoff(_refreshAttempts, _options.MinReconnectDelay, _options.MaxReconnectDelay);
                _refreshAttempts++;
                _refreshTimer?.Dispose();
                _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, Timeout.Infinite);
            }
        }

        private void HandleRefreshReply(RefreshResult result)
        {
            if (_state != ClientState.Connected)
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
                _refreshTimer = new Timer(_ => _ = RefreshConnectionTokenAsync(), null, delay, Timeout.Infinite);
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
                    ErrorCodes.ConnectionClosed,
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
                    ServerSubscribing?.Invoke(this, new ServerSubscribingEventArgs(channel));
                }

                _serverSubscriptions[channel] = new ServerSubscription
                {
                    Offset = sub.Offset,
                    Epoch = sub.Epoch,
                    Recoverable = sub.Recoverable
                };

                StreamPosition? streamPosition = null;
                if (sub.Positioned)
                {
                    streamPosition = new StreamPosition(sub.Offset, sub.Epoch);
                }

                ServerSubscribed?.Invoke(this, new ServerSubscribedEventArgs(
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
                ServerUnsubscribed?.Invoke(this, new ServerUnsubscribedEventArgs(channel));
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

            StreamPosition? streamPosition = null;
            if (sub.Positioned)
            {
                streamPosition = new StreamPosition(sub.Offset, sub.Epoch);
            }

            // Fire ServerSubscribing event for new subscriptions
            if (!wasRecovering)
            {
                ServerSubscribing?.Invoke(this, new ServerSubscribingEventArgs(channel));
            }

            ServerSubscribed?.Invoke(this, new ServerSubscribedEventArgs(
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
                ServerUnsubscribed?.Invoke(this, new ServerUnsubscribedEventArgs(channel));
            }
        }

        private void OnError(string type, Exception exception)
        {
            Error?.Invoke(this, new ErrorEventArgs(type, 0, exception.Message, false, exception));
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

        internal static PublicationEventArgs CreatePublicationArgs(string channel, Publication pub)
        {
            return CreatePublicationArgs(channel, pub, pub.Data.ToByteArray());
        }

        internal static PublicationEventArgs CreatePublicationArgs(string channel, Publication pub, byte[] data)
        {
            ClientInfo? info = null;
            if (pub.Info != null)
            {
                info = new ClientInfo(
                    pub.Info.User,
                    pub.Info.Client,
                    pub.Info.ConnInfo.ToByteArray(),
                    pub.Info.ChanInfo.ToByteArray()
                );
            }

            var tags = pub.Tags.Count > 0
                ? new Dictionary<string, string>(pub.Tags)
                : null;

            return new PublicationEventArgs(
                channel,
                data,
                info,
                pub.Offset > 0 ? pub.Offset : null,
                tags
            );
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
            _stateLock?.Dispose();
            _reconnectCts?.Dispose();
            _pingTimer?.Dispose();
            _refreshTimer?.Dispose();
        }
    }
}
