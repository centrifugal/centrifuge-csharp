#if NET6_0_OR_GREATER
using System;
using System.Buffers;
using System.Collections.Generic;
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
        /// <summary>Counter for JS socket IDs. It is static, so IDs never repeat within the runtime.</summary>
        private static int _nextSocketId;

        private readonly string _endpoint;
        private readonly string _subProtocol;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger? _logger;
        private IJSObjectReference? _jsModule;
        private DotNetObjectReference<BrowserWebSocketTransport>? _dotnetRef;

        /// <summary>
        /// ID JS registers the socket under, assigned right after connect is dispatched. The cleanup
        /// that takes it closes that socket, even when the open is given up before connect returns.
        /// </summary>
        private int _socketId;
        private int _disposed;
        private int _cleanupStarted;
        private volatile bool _isOpen;
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
        public event EventHandler<IReadOnlyList<byte[]>>? MessageReceived;

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
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BrowserWebSocketTransport));
            }

            try
            {
                _logger?.LogDebug("Loading JS module...");
                // Load the JavaScript module with cache busting parameter
                _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    cancellationToken,
                    "./_content/Centrifugal.Centrifuge/centrifuge-websocket.js?v=2"
                ).ConfigureAwait(false);
                ThrowIfDisposed();
                _logger?.LogDebug("JS module loaded");

                // Create .NET object reference for callbacks
                _dotnetRef = DotNetObjectReference.Create(this);
                _logger?.LogDebug("DotNetObjectReference created");

                // Create completion source for open event
                _openTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Connect via JavaScript (call on global window object)
                _logger?.LogDebug("Calling CentrifugeWebSocket.connect...");
                var socketId = Interlocked.Increment(ref _nextSocketId);
                var connect = _jsRuntime.InvokeVoidAsync(
                    "CentrifugeWebSocket.connect",
                    cancellationToken,
                    socketId,
                    _endpoint,
                    _subProtocol,
                    _dotnetRef,
                    _logger?.IsEnabled(LogLevel.Debug) ?? false
                );
                _socketId = socketId;
                await connect.ConfigureAwait(false);
                ThrowIfDisposed();
                _logger?.LogDebug($"Socket created with ID: {socketId}");

                // Wait for connection to open
                _logger?.LogDebug("Waiting for OnOpen callback...");
                if (!await Utilities.CompletesBeforeCancellationAsync(_openTcs.Task, cancellationToken).ConfigureAwait(false))
                {
                    _logger?.LogDebug("Timeout waiting for OnOpen");
                    _openTcs.TrySetException(new TimeoutException("Timeout waiting for WebSocket connection to open"));
                }

                await _openTcs.Task.ConfigureAwait(false);
                // Note: _isOpen is already set to true in OnOpen() callback before the event fires
                _logger?.LogDebug($"OpenAsync completed successfully, socket {_socketId} is open");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"OpenAsync failed with exception: {ex.Message}");
                _ = CleanupAsync();
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
                _ = CloseAfterFailedSendAsync(_socketId);
                throw new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "Failed to send data", true, ex);
            }
        }

        /// <summary>Closes the socket after a send error; Closed is then raised from its onclose.</summary>
        private async Task CloseAfterFailedSendAsync(int socketId)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.close", socketId, 1000, "send failed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Close after failed send failed: {ex.Message}");
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
                await CleanupAsync().ConfigureAwait(false);
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
        /// JavaScript callback when WebSocket opens. It races the OpenAsync timeout to complete
        /// _openTcs, and Opened is raised only if this callback gets there first.
        /// </summary>
        [JSInvokable]
        public void OnOpen()
        {
            _logger?.LogDebug($"OnOpen called for socket {_socketId}");
            // Discard stale callback if CleanupAsync already ran (e.g. OpenAsync timed out).
            if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;
            // Set _isOpen BEFORE firing events so handlers can use the transport immediately
            _isOpen = true;
            var result = _openTcs?.TrySetResult(true);
            _logger?.LogDebug($"OnOpen - TrySetResult returned: {result}, _isOpen set to true");
            if (result == false)
            {
                _isOpen = false;
                return;
            }
            Opened?.Invoke(this, EventArgs.Empty);
            _logger?.LogDebug($"OnOpen - Opened event fired");
        }

        /// <summary>
        /// JavaScript callback when WebSocket receives a message. The messages it carries are raised
        /// in order as one frame. When one is malformed, those before it are still raised, then the
        /// transport closes with the parse error and the client reconnects rather than skip data.
        /// </summary>
        /// <param name="data">Message data as byte array.</param>
        [JSInvokable]
        public void OnMessage(byte[] data)
        {
            if (data == null || data.Length == 0 ||
                System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0)
            {
                return;
            }

            try
            {
                var frame = new List<byte[]>();
                try
                {
                    if (VarintCodec.ReadCompleteMessages(data, data.Length, frame) != data.Length)
                        throw new IOException("Truncated message in WebSocket frame");
                }
                finally
                {
                    if (frame.Count > 0) MessageReceived?.Invoke(this, frame);
                }
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;
                Error?.Invoke(this, ex);
                _isOpen = false;
                Closed?.Invoke(this, new TransportClosedEventArgs());
                _ = CleanupAsync();
            }
        }

        /// <summary>
        /// Until the socket has opened, a failure is reported only by failing the open, the same
        /// as in the native transport. The client then reports it and schedules a retry; raising
        /// Error or Closed as well would report it twice and restart the reconnect. Returns true in
        /// that case. Later callbacks from a socket that never opened are ignored too.
        /// </summary>
        private bool FailOpenUnlessOpened(Exception error)
        {
            if (_openTcs is { } open && open.Task.Status == TaskStatus.RanToCompletion) return false;
            _openTcs?.TrySetException(error);
            return true;
        }

        /// <summary>
        /// JavaScript callback when WebSocket encounters an error.
        /// </summary>
        /// <param name="message">Error message.</param>
        [JSInvokable]
        public void OnError(string message)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;
            var error = new Exception(message ?? "WebSocket error");
            if (FailOpenUnlessOpened(new CentrifugeException(CentrifugeErrorCodes.TransportClosed, error.Message, true, error))) return;
            Error?.Invoke(this, error);
        }

        /// <summary>
        /// JavaScript callback when WebSocket closes.
        /// </summary>
        /// <param name="codeObj">Close code.</param>
        /// <param name="reasonObj">Close reason.</param>
        [JSInvokable]
        public void OnClose(object? codeObj, object? reasonObj)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;

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

            if (FailOpenUnlessOpened(new CentrifugeException(CentrifugeErrorCodes.TransportClosed,
                    $"WebSocket closed before opening: {code} {reason}", true))) return;
            _isOpen = false;
            var args = new TransportClosedEventArgs(code, reason);
            _logger?.LogDebug($"OnClose - created args with Code: {args.Code}, Reason: '{args.Reason}'");

            Closed?.Invoke(this, args);
            _ = CleanupAsync();
        }

        /// <summary>
        /// Releases the transport's resources. Each one is swapped out and released only once, so
        /// a second cleanup can still release what OpenAsync created after the first (Dispose during
        /// open). The socket is closed first unless <paramref name="alreadyClosed"/> is set. Callbacks
        /// that arrive after the first cleanup are ignored.
        /// </summary>
        private async Task CleanupAsync(bool alreadyClosed = false)
        {
            System.Threading.Interlocked.Exchange(ref _cleanupStarted, 1);
            _isOpen = false;
            _openTcs?.TrySetCanceled();

            var socketId = System.Threading.Interlocked.Exchange(ref _socketId, 0);
            if (socketId > 0)
            {
                if (!alreadyClosed)
                {
                    try
                    {
                        _logger?.LogDebug($"Closing socket {socketId}");
                        await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.close", socketId, 1000, "Client cleanup").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Error closing socket: {ex.Message}");
                    }
                }

                try
                {
                    await _jsRuntime.InvokeVoidAsync("CentrifugeWebSocket.dispose", socketId).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            System.Threading.Interlocked.Exchange(ref _dotnetRef, null)?.Dispose();

            var jsModule = System.Threading.Interlocked.Exchange(ref _jsModule, null);
            if (jsModule != null)
            {
                try
                {
                    await jsModule.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        /// <summary>Stops OpenAsync on a concurrent Dispose; the open's error path releases what it just created.</summary>
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(BrowserWebSocketTransport));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            _ = CloseAsync();
        }
    }
}
#endif
