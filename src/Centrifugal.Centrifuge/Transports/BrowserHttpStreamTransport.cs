#if NET6_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// Browser HTTP streaming transport for Blazor WebAssembly.
    /// Uses JavaScript interop to access browser's Fetch API with ReadableStream.
    /// </summary>
    internal class BrowserHttpStreamTransport : ITransport
    {
        private readonly string _endpoint;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger? _logger;
        private IJSObjectReference? _jsModule;
        private DotNetObjectReference<BrowserHttpStreamTransport>? _dotnetRef;
        private int _streamId;
        private bool _disposed;
        private bool _isOpen;
        private TaskCompletionSource<bool>? _openTcs;
        private MemoryStream _chunkBuffer = new();
        private readonly object _bufferLock = new object();

        /// <inheritdoc/>
        public CentrifugeTransportType Type => CentrifugeTransportType.HttpStream;

        /// <inheritdoc/>
        public string Name => "http_stream";

        /// <inheritdoc/>
        public bool UsesEmulation => true;

        /// <inheritdoc/>
        public event EventHandler? Opened;

        /// <inheritdoc/>
        public event EventHandler<byte[]>? MessageReceived;

        /// <inheritdoc/>
        public event EventHandler<TransportClosedEventArgs>? Closed;

        /// <inheritdoc/>
        public event EventHandler<Exception>? Error;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrowserHttpStreamTransport"/> class.
        /// </summary>
        /// <param name="endpoint">HTTP stream endpoint URL.</param>
        /// <param name="jsRuntime">JavaScript runtime for interop.</param>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        public BrowserHttpStreamTransport(string endpoint, IJSRuntime jsRuntime, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            _endpoint = endpoint;
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
                // Load the JavaScript module (this adds CentrifugeHttpStream to window)
                // Add version parameter to bust cache
                _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    "./_content/Centrifugal.Centrifuge/centrifuge-httpstream.js?v=2"
                ).ConfigureAwait(false);
                _logger?.LogDebug("JS module loaded");

                // Create .NET object reference for callbacks
                _dotnetRef = DotNetObjectReference.Create(this);

                // Create completion source for open event
                _openTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Prepare initial data with varint delimiter
                using var ms = new MemoryStream();
                VarintCodec.WriteDelimitedMessage(ms, initialData ?? Array.Empty<byte>());
                byte[] delimitedData = ms.ToArray();

                // Connect via JavaScript (call on global window object, not the module)
                _logger?.LogDebug("Calling CentrifugeHttpStream.connect...");
                _streamId = await _jsRuntime.InvokeAsync<int>(
                    "CentrifugeHttpStream.connect",
                    cancellationToken,
                    _endpoint,
                    delimitedData,
                    _dotnetRef,
                    _logger?.IsEnabled(LogLevel.Debug) ?? false
                ).ConfigureAwait(false);
                _logger?.LogDebug($"Stream created with ID: {_streamId}");

                // Wait for connection to open or error
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                _logger?.LogDebug("Waiting for OnOpen callback...");
                var completedTask = await Task.WhenAny(_openTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completedTask != _openTcs.Task)
                {
                    _logger?.LogDebug("Timeout waiting for OnOpen");
                    throw new TimeoutException("Timeout waiting for HTTP stream connection to open");
                }

                await _openTcs.Task.ConfigureAwait(false);
                // Note: _isOpen is already set to true in OnOpen() callback before the event fires
                _logger?.LogDebug($"OpenAsync completed successfully, stream {_streamId} is open");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"OpenAsync failed with exception: {ex.Message}");
                await CleanupAsync().ConfigureAwait(false);
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, $"Failed to open HTTP stream connection: {ex.Message}", true, ex);
            }
        }

        /// <inheritdoc/>
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("HTTP Stream transport uses emulation mode. Use SendEmulationAsync instead.");
        }

        /// <inheritdoc/>
        public async Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default)
        {
            if (!_isOpen)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "HTTP stream transport is not open");
            }

            try
            {
                // Create EmulationRequest
                var emulationRequest = new EmulationRequest
                {
                    Session = session,
                    Node = node,
                    Data = ByteString.CopyFrom(data)
                };

                var requestBytes = emulationRequest.ToByteArray();

                // Send to emulation endpoint
                await _jsRuntime.InvokeVoidAsync(
                    "CentrifugeHttpStream.sendEmulation",
                    cancellationToken,
                    emulationEndpoint,
                    session,
                    node,
                    requestBytes
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "Failed to send data via HTTP stream", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task CloseAsync()
        {
            _logger?.LogDebug($"CloseAsync called, _isOpen: {_isOpen}");
            if (!_isOpen)
            {
                // Still cleanup resources even if not marked as open (e.g., if error occurred during handshake)
                if (_streamId > 0 || _dotnetRef != null || _jsModule != null)
                {
                    await CleanupAsync().ConfigureAwait(false);
                }
                return;
            }

            _isOpen = false; // Mark as closed immediately to prevent race conditions

            try
            {
                if (_streamId > 0)
                {
                    _logger?.LogDebug($"Closing stream {_streamId}");
                    await _jsRuntime.InvokeVoidAsync("CentrifugeHttpStream.close", _streamId)
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
                // Pass alreadyClosed=true since we already closed the stream above
                await CleanupAsync(alreadyClosed: true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// JavaScript callback when HTTP stream opens.
        /// </summary>
        [JSInvokable]
        public void OnOpen()
        {
            _logger?.LogDebug($"OnOpen called for stream {_streamId}");
            // Set _isOpen BEFORE firing events so handlers can use the transport immediately
            _isOpen = true;
            var result = _openTcs?.TrySetResult(true);
            _logger?.LogDebug($"OnOpen - TrySetResult returned: {result}, _isOpen set to true");
            Opened?.Invoke(this, EventArgs.Empty);
            _logger?.LogDebug($"OnOpen - Opened event fired");
        }

        /// <summary>
        /// JavaScript callback when HTTP stream receives a chunk.
        /// </summary>
        /// <param name="base64Chunk">Chunk data as base64-encoded string.</param>
        [JSInvokable]
        public void OnChunk(string base64Chunk)
        {
            if (string.IsNullOrEmpty(base64Chunk))
            {
                return;
            }

            try
            {
                // Decode base64 to byte array
                byte[] chunk = Convert.FromBase64String(base64Chunk);

                lock (_bufferLock)
                {
                    // Append chunk to buffer
                    _chunkBuffer.Write(chunk, 0, chunk.Length);

                    // Try to extract varint-delimited messages
                    _chunkBuffer.Position = 0;
                    byte[] tempBuffer = new byte[8192];
                    var processedMessages = new System.Collections.Generic.List<byte[]>();

                    while (_chunkBuffer.Position < _chunkBuffer.Length)
                    {
                        long startPos = _chunkBuffer.Position;
                        byte[]? message = VarintCodec.ReadDelimitedMessage(_chunkBuffer, tempBuffer, CancellationToken.None);

                        if (message == null)
                        {
                            // Incomplete message - keep remaining bytes in buffer
                            long remainingBytes = _chunkBuffer.Length - startPos;
                            var remaining = new byte[remainingBytes];
                            _chunkBuffer.Position = startPos;
                            _chunkBuffer.Read(remaining, 0, (int)remainingBytes);

                            // Reset buffer with remaining bytes
                            _chunkBuffer = new MemoryStream();
                            _chunkBuffer.Write(remaining, 0, remaining.Length);
                            break;
                        }

                        processedMessages.Add(message);
                    }

                    // If we processed all messages, reset buffer
                    if (_chunkBuffer.Position >= _chunkBuffer.Length)
                    {
                        _chunkBuffer = new MemoryStream();
                    }

                    // Dispatch messages outside the lock
                    foreach (var message in processedMessages)
                    {
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
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
        }

        /// <summary>
        /// JavaScript callback when HTTP stream encounters an error.
        /// </summary>
        /// <param name="statusCode">HTTP status code (0 if not HTTP error).</param>
        /// <param name="message">Error message.</param>
        [JSInvokable]
        public void OnError(int statusCode, string message)
        {
            var exception = new Exception(message ?? "HTTP stream error");
            Error?.Invoke(this, exception);

            // If we haven't opened yet, fail the open operation
            if (_openTcs != null && !_openTcs.Task.IsCompleted)
            {
                // Map HTTP status code to close code
                int closeCode;
                if (statusCode == 400 || statusCode == 401 || statusCode == 403 ||
                    statusCode == 404 || statusCode == 405)
                {
                    closeCode = CentrifugeDisconnectedCodes.BadProtocol;
                }
                else if (statusCode > 0)
                {
                    closeCode = CentrifugeConnectingCodes.TransportClosed;
                }
                else
                {
                    closeCode = 0;
                }

                var transportException = new CentrifugeException(
                    CentrifugeErrorCodes.TransportClosed,
                    message ?? "HTTP stream error",
                    true,
                    exception
                );
                _openTcs.TrySetException(transportException);

                // Also trigger close event
                Closed?.Invoke(this, new TransportClosedEventArgs(closeCode, message ?? "HTTP stream error", exception));
            }
        }

        /// <summary>
        /// JavaScript callback when HTTP stream closes.
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

        private async Task CleanupAsync(bool alreadyClosed = false)
        {
            _logger?.LogDebug($"CleanupAsync called for stream {_streamId}, alreadyClosed: {alreadyClosed}");
            _isOpen = false;
            _openTcs?.TrySetCanceled();

            lock (_bufferLock)
            {
                _chunkBuffer?.Dispose();
                _chunkBuffer = new MemoryStream();
            }

            if (_streamId > 0)
            {
                // Only close if not already closed (e.g., when cleanup is called from error paths)
                if (!alreadyClosed)
                {
                    try
                    {
                        // Close/abort the HTTP stream first if it's still active
                        // This is important to prevent resource leaks when errors occur during connection
                        _logger?.LogDebug($"Closing stream {_streamId}");
                        await _jsRuntime.InvokeVoidAsync("CentrifugeHttpStream.close", _streamId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Error closing stream: {ex.Message}");
                        // Ignore cleanup errors, but try to dispose anyway
                    }
                }

                try
                {
                    _logger?.LogDebug($"Disposing stream {_streamId}");
                    await _jsRuntime.InvokeVoidAsync("CentrifugeHttpStream.dispose", _streamId).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cleanup errors
                }

                _streamId = 0;
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
