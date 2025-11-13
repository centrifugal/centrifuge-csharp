#if NET6_0_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// Browser WebSocket transport for Blazor WebAssembly.
    /// Uses JavaScript interop to access browser's native WebSocket API.
    /// </summary>
    internal class BrowserWebSocketTransport : ITransport
    {
        private readonly string _endpoint;
        private readonly string _subProtocol;
        private readonly IJSRuntime _jsRuntime;
        private IJSObjectReference? _jsModule;
        private DotNetObjectReference<BrowserWebSocketTransport>? _dotnetRef;
        private int _socketId;
        private bool _disposed;
        private bool _isOpen;
        private TaskCompletionSource<bool>? _openTcs;

        /// <inheritdoc/>
        public CentrifugeTransportType Type => CentrifugeTransportType.WebSocket;

        /// <inheritdoc/>
        public string Name => "websocket";

        /// <inheritdoc/>
        public bool UsesEmulation => false;

        /// <inheritdoc/>
        public event EventHandler? Opened;

        /// <inheritdoc/>
        public event EventHandler<byte[]>? MessageReceived;

        /// <inheritdoc/>
        public event EventHandler<TransportClosedEventArgs>? Closed;

        /// <inheritdoc/>
        public event EventHandler<Exception>? Error;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrowserWebSocketTransport"/> class.
        /// </summary>
        /// <param name="endpoint">WebSocket endpoint URL.</param>
        /// <param name="jsRuntime">JavaScript runtime for interop.</param>
        public BrowserWebSocketTransport(string endpoint, IJSRuntime jsRuntime)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            _endpoint = endpoint;
            _subProtocol = "centrifuge-protobuf";
            _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        }

        /// <inheritdoc/>
        public async Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null)
        {
            if (_isOpen)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            try
            {
                // Load the JavaScript module (inline to avoid file loading issues)
                _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./_content/Centrifugal.Centrifuge/centrifuge-websocket.js"
                ).ConfigureAwait(false);

                // Create .NET object reference for callbacks
                _dotnetRef = DotNetObjectReference.Create(this);

                // Create completion source for open event
                _openTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Connect via JavaScript
                _socketId = await _jsModule.InvokeAsync<int>(
                    "CentrifugeWebSocket.connect",
                    cancellationToken,
                    _endpoint,
                    _subProtocol,
                    _dotnetRef
                ).ConfigureAwait(false);

                // Wait for connection to open
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                var completedTask = await Task.WhenAny(_openTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != _openTcs.Task)
                {
                    throw new TimeoutException("Timeout waiting for WebSocket connection to open");
                }

                await _openTcs.Task.ConfigureAwait(false);
                _isOpen = true;
            }
            catch (Exception ex)
            {
                await CleanupAsync().ConfigureAwait(false);
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "Failed to connect WebSocket", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (!_isOpen || _jsModule == null)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "WebSocket is not open");
            }

            try
            {
                // Use a memory stream to build the complete message with varint-delimited frames
                using var ms = new MemoryStream();
                VarintCodec.WriteDelimitedMessage(ms, data);
                byte[] message = ms.ToArray();

                await _jsModule.InvokeVoidAsync(
                    "CentrifugeWebSocket.send",
                    cancellationToken,
                    _socketId,
                    message
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "Failed to send data", true, ex);
            }
        }

        /// <inheritdoc/>
        public Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("WebSocket transport does not use emulation mode");
        }

        /// <inheritdoc/>
        public async Task CloseAsync()
        {
            if (!_isOpen) return;

            try
            {
                if (_jsModule != null && _socketId > 0)
                {
                    await _jsModule.InvokeVoidAsync("CentrifugeWebSocket.close", _socketId, 1000, "Client disconnect")
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors during close
            }
            finally
            {
                await CleanupAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// JavaScript callback when WebSocket opens.
        /// </summary>
        [JSInvokable]
        public void OnOpen()
        {
            _openTcs?.TrySetResult(true);
            Opened?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// JavaScript callback when WebSocket receives a message.
        /// </summary>
        /// <param name="data">Message data as byte array.</param>
        [JSInvokable]
        public void OnMessage(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            try
            {
                // Process varint-delimited messages
                using var ms = new MemoryStream(data);
                byte[] tempBuffer = new byte[8192];

                while (ms.Position < ms.Length)
                {
                    byte[]? message = VarintCodec.ReadDelimitedMessage(ms, tempBuffer, CancellationToken.None);
                    if (message == null) break;

                    // Dispatch message on thread pool to avoid blocking
                    var messageCopy = message;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            MessageReceived?.Invoke(this, messageCopy);
                        }
                        catch (Exception ex)
                        {
                            Error?.Invoke(this, ex);
                        }
                    }, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
        }

        /// <summary>
        /// JavaScript callback when WebSocket encounters an error.
        /// </summary>
        /// <param name="message">Error message.</param>
        [JSInvokable]
        public void OnError(string message)
        {
            Error?.Invoke(this, new Exception(message ?? "WebSocket error"));
        }

        /// <summary>
        /// JavaScript callback when WebSocket closes.
        /// </summary>
        /// <param name="code">Close code.</param>
        /// <param name="reason">Close reason.</param>
        [JSInvokable]
        public void OnClose(int code, string reason)
        {
            _isOpen = false;
            Closed?.Invoke(this, new TransportClosedEventArgs(code, reason));
            _ = CleanupAsync();
        }

        private async Task CleanupAsync()
        {
            _isOpen = false;
            _openTcs?.TrySetCanceled();

            if (_jsModule != null && _socketId > 0)
            {
                try
                {
                    await _jsModule.InvokeVoidAsync("CentrifugeWebSocket.dispose", _socketId).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            _dotnetRef?.Dispose();
            _dotnetRef = null;

            if (_jsModule != null)
            {
                try
                {
                    await _jsModule.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore disposal errors
                }
                _jsModule = null;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ = CloseAsync();
        }
    }
}
#endif
