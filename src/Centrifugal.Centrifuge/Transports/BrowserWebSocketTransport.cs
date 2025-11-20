#if NET6_0_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger? _logger;
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
        /// <param name="logger">Optional logger for diagnostic output.</param>
        public BrowserWebSocketTransport(string endpoint, IJSRuntime jsRuntime, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            _endpoint = endpoint;
            _subProtocol = "centrifuge-protobuf";
            _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null)
        {
            _logger?.LogDebug($"OpenAsync called, endpoint: {_endpoint}");
            if (_isOpen)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            try
            {
                _logger?.LogDebug("Loading JS module...");
                // Load the JavaScript module with cache busting parameter
                _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./_content/Centrifugal.Centrifuge/centrifuge-websocket.js"
                ).ConfigureAwait(false);
                _logger?.LogDebug("JS module loaded");

                // Create .NET object reference for callbacks
                _dotnetRef = DotNetObjectReference.Create(this);
                _logger?.LogDebug("DotNetObjectReference created");

                // Create completion source for open event
                _openTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Connect via JavaScript (call on global window object)
                _logger?.LogDebug("Calling CentrifugeWebSocket.connect...");
                _socketId = await _jsRuntime.InvokeAsync<int>(
                    "CentrifugeWebSocket.connect",
                    cancellationToken,
                    _endpoint,
                    _subProtocol,
                    _dotnetRef,
                    _logger?.IsEnabled(LogLevel.Debug) ?? false
                ).ConfigureAwait(false);
                _logger?.LogDebug($"Socket created with ID: {_socketId}");

                // Wait for connection to open
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                _logger?.LogDebug("Waiting for OnOpen callback...");
                var completedTask = await Task.WhenAny(_openTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != _openTcs.Task)
                {
                    _logger?.LogDebug("Timeout waiting for OnOpen");
                    throw new TimeoutException("Timeout waiting for WebSocket connection to open");
                }

                await _openTcs.Task.ConfigureAwait(false);
                // Note: _isOpen is already set to true in OnOpen() callback before the event fires
                _logger?.LogDebug($"OpenAsync completed successfully, socket {_socketId} is open");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"OpenAsync failed with exception: {ex.Message}");
                await CleanupAsync().ConfigureAwait(false);
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "Failed to connect WebSocket", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            _logger?.LogDebug($"SendAsync called, socket ID: {_socketId}, _isOpen: {_isOpen}, data length: {data.Length}");
            if (!_isOpen)
            {
                _logger?.LogDebug($"SendAsync failed - WebSocket is not open");
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "WebSocket is not open");
            }

            try
            {
                _logger?.LogDebug($"Sending {data.Length} bytes to socket {_socketId}");
                await _jsRuntime.InvokeVoidAsync(
                    "CentrifugeWebSocket.send",
                    cancellationToken,
                    _socketId,
                    data
                ).ConfigureAwait(false);
                _logger?.LogDebug($"Send completed for socket {_socketId}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Send failed with exception: {ex.Message}");
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
            _logger?.LogDebug($"CloseAsync called, _isOpen: {_isOpen}");
            if (!_isOpen)
            {
                // Still cleanup resources even if not marked as open (e.g., if error occurred during handshake)
                if (_socketId > 0 || _dotnetRef != null || _jsModule != null)
                {
                    await CleanupAsync().ConfigureAwait(false);
                }
                return;
            }

            _isOpen = false; // Mark as closed immediately to prevent race conditions

            try
            {
                if (_socketId > 0)
                {
                    _logger?.LogDebug($"Closing socket {_socketId} with code 1000");
                    await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.close", _socketId, 1000, "Client disconnect")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Error during close: {ex.Message}");
                // Ignore errors during close
            }
            finally
            {
                // Pass alreadyClosed=true since we already closed the socket above
                await CleanupAsync(alreadyClosed: true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// JavaScript callback when WebSocket opens.
        /// </summary>
        [JSInvokable]
        public void OnOpen()
        {
            _logger?.LogDebug($"OnOpen called for socket {_socketId}");
            // Set _isOpen BEFORE firing events so handlers can use the transport immediately
            _isOpen = true;
            var result = _openTcs?.TrySetResult(true);
            _logger?.LogDebug($"OnOpen - TrySetResult returned: {result}, _isOpen set to true");
            Opened?.Invoke(this, EventArgs.Empty);
            _logger?.LogDebug($"OnOpen - Opened event fired");
        }

        /// <summary>
        /// JavaScript callback when WebSocket receives a message.
        /// </summary>
        /// <param name="base64Data">Message data as base64-encoded string.</param>
        [JSInvokable]
        public void OnMessage(string base64Data)
        {
            if (string.IsNullOrEmpty(base64Data))
            {
                return;
            }

            try
            {
                // Decode base64 to byte array
                byte[] data = Convert.FromBase64String(base64Data);

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
        /// <param name="codeObj">Close code.</param>
        /// <param name="reasonObj">Close reason.</param>
        [JSInvokable]
        public void OnClose(object? codeObj, object? reasonObj)
        {
            // Log directly via JavaScript to ensure it shows up
            _ = _jsRuntime.InvokeVoidAsync("console.log", "[C#] OnClose called with codeObj:", codeObj, "reasonObj:", reasonObj);

            _logger?.LogDebug($"OnClose START - codeObj type: {codeObj?.GetType()?.Name ?? "null"}, value: {codeObj}, reasonObj type: {reasonObj?.GetType()?.Name ?? "null"}, value: '{reasonObj}'");

            int? code = null;
            if (codeObj != null)
            {
                if (codeObj is int intCode)
                {
                    code = intCode;
                }
                else if (int.TryParse(codeObj.ToString(), out int parsedCode))
                {
                    code = parsedCode;
                }
            }

            string reason = reasonObj?.ToString() ?? string.Empty;

            _logger?.LogDebug($"OnClose - parsed code: {code}, reason: '{reason}'");

            _isOpen = false;
            var args = new TransportClosedEventArgs(code, reason);
            _logger?.LogDebug($"OnClose - created args with Code: {args.Code}, Reason: '{args.Reason}'");

            _ = _jsRuntime.InvokeVoidAsync("console.log", "[C#] Firing Closed event with Code:", args.Code, "Reason:", args.Reason);

            Closed?.Invoke(this, args);
            _ = CleanupAsync();
        }

        private async Task CleanupAsync(bool alreadyClosed = false)
        {
            _logger?.LogDebug($"CleanupAsync called for socket {_socketId}, alreadyClosed: {alreadyClosed}");
            _isOpen = false;
            _openTcs?.TrySetCanceled();

            if (_socketId > 0)
            {
                // Only close if not already closed (e.g., when cleanup is called from error paths)
                if (!alreadyClosed)
                {
                    try
                    {
                        // Close the WebSocket first if it's still open/connecting
                        // This is important to prevent resource leaks when errors occur during connection
                        _logger?.LogDebug($"Closing socket {_socketId} with code 1006");
                        await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.close", _socketId, 1006, "Abnormal closure").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Error closing socket: {ex.Message}");
                        // Ignore cleanup errors, but try to dispose anyway
                    }
                }

                try
                {
                    _logger?.LogDebug($"Disposing socket {_socketId}");
                    await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.dispose", _socketId).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cleanup errors
                }

                _socketId = 0;
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

            _logger?.LogDebug("CleanupAsync completed");
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
