#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
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
        /// <summary>Counter for JS stream IDs. It is static, so IDs never repeat within the runtime.</summary>
        private static int _nextStreamId;

        private readonly string _endpoint;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger? _logger;
        private IJSObjectReference? _jsModule;
        private DotNetObjectReference<BrowserHttpStreamTransport>? _dotnetRef;

        /// <summary>
        /// ID JS registers the stream under, assigned right after connect is dispatched. The cleanup
        /// that takes it closes that stream, even when the open is given up before connect returns.
        /// </summary>
        private int _streamId;
        private int _disposed;
        private int _cleanupStarted;
        private volatile bool _isOpen;
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
        public event EventHandler<IReadOnlyList<byte[]>>? MessageReceived;

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
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BrowserHttpStreamTransport));
            }

            try
            {
                _logger?.LogDebug("Loading JS module...");
                // Load the JavaScript module (this adds CentrifugeHttpStream to window)
                // Add version parameter to bust cache
                _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    cancellationToken,
                    "./_content/Centrifugal.Centrifuge/centrifuge-httpstream.js?v=5"
                ).ConfigureAwait(false);
                ThrowIfDisposed();
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
                var streamId = Interlocked.Increment(ref _nextStreamId);
                var connect = _jsRuntime.InvokeVoidAsync(
                    "CentrifugeHttpStream.connect",
                    cancellationToken,
                    streamId,
                    _endpoint,
                    delimitedData,
                    _dotnetRef,
                    _logger?.IsEnabled(LogLevel.Debug) ?? false
                );
                _streamId = streamId;
                await connect.ConfigureAwait(false);
                ThrowIfDisposed();
                _logger?.LogDebug($"Stream created with ID: {streamId}");

                // Wait for connection to open or error
                _logger?.LogDebug("Waiting for OnOpen callback...");
                if (!await Utilities.CompletesBeforeCancellationAsync(_openTcs.Task, cancellationToken).ConfigureAwait(false))
                {
                    _logger?.LogDebug("Timeout waiting for OnOpen");
                    _openTcs.TrySetException(new TimeoutException("Timeout waiting for HTTP stream connection to open"));
                }

                await _openTcs.Task.ConfigureAwait(false);
                // Note: _isOpen is already set to true in OnOpen() callback before the event fires
                _logger?.LogDebug($"OpenAsync completed successfully, stream {_streamId} is open");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"OpenAsync failed with exception: {ex.Message}");
                _ = CleanupAsync();
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
                var status = await _jsRuntime.InvokeAsync<int>(
                    "CentrifugeHttpStream.sendEmulation",
                    cancellationToken,
                    _streamId,
                    emulationEndpoint,
                    requestBytes
                ).ConfigureAwait(false);
                if (status < 200 || status >= 300) throw new EmulationStatusException(status);
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
                await CleanupAsync().ConfigureAwait(false);
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
        /// JavaScript callback when HTTP stream opens. It races the OpenAsync timeout to complete
        /// _openTcs, and Opened is raised only if this callback gets there first.
        /// </summary>
        [JSInvokable]
        public void OnOpen()
        {
            _logger?.LogDebug($"OnOpen called for stream {_streamId}");
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
        /// JavaScript callback when HTTP stream receives a chunk. All complete messages in the buffer
        /// are raised in order as one frame, outside the lock; a partial message at the end waits for
        /// the next chunk. When a message is malformed, those before it are still raised, but the
        /// stream can't be resynchronized, so the transport closes and the client reconnects.
        /// </summary>
        /// <param name="chunk">Chunk data as byte array.</param>
        [JSInvokable]
        public void OnChunk(byte[] chunk)
        {
            if (chunk == null || chunk.Length == 0 ||
                System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0)
            {
                return;
            }

            var processedMessages = new List<byte[]>();
            try
            {
                try
                {
                    lock (_bufferLock)
                    {
                        // Add the chunk after any partial message left from earlier chunks
                        _chunkBuffer.Write(chunk, 0, chunk.Length);

                        var buffer = _chunkBuffer.GetBuffer();
                        int length = (int)_chunkBuffer.Length;
                        int consumed = VarintCodec.ReadCompleteMessages(buffer, length, processedMessages);
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
                        _chunkBuffer.SetLength(length - consumed);
                        if (_chunkBuffer.Length <= VarintCodec.ReceiveBufferSize && _chunkBuffer.Capacity > VarintCodec.ReceiveBufferSize)
                            _chunkBuffer.Capacity = VarintCodec.ReceiveBufferSize;
                    }
                }
                finally
                {
                    if (processedMessages.Count > 0) MessageReceived?.Invoke(this, processedMessages);
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
        /// JavaScript callback when HTTP stream encounters an error. Before the open completes it
        /// only fails the open (see FailOpenUnlessOpened).
        /// </summary>
        /// <param name="statusCode">HTTP status code (0 if not HTTP error).</param>
        /// <param name="message">Error message.</param>
        [JSInvokable]
        public void OnError(int statusCode, string message)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;
            var exception = new Exception(message ?? "HTTP stream error");
            if (FailOpenUnlessOpened(new CentrifugeException(CentrifugeErrorCodes.TransportClosed, exception.Message, true, exception))) return;
            Error?.Invoke(this, exception);
        }

        /// <summary>
        /// Until the stream has opened, a failure is reported only by failing the open, the same
        /// as in the native transport. The client then reports it and schedules a retry; raising
        /// Error or Closed as well would report it twice and restart the reconnect. Returns true in
        /// that case. Later callbacks from a stream that never opened are ignored too.
        /// </summary>
        private bool FailOpenUnlessOpened(Exception error)
        {
            if (_openTcs is { } open && open.Task.Status == TaskStatus.RanToCompletion) return false;
            _openTcs?.TrySetException(error);
            return true;
        }

        /// <summary>
        /// JavaScript callback when HTTP stream closes.
        /// </summary>
        /// <param name="code">Close code.</param>
        /// <param name="reason">Close reason.</param>
        [JSInvokable]
        public void OnClose(int code, string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _cleanupStarted, 0, 0) != 0) return;
            if (FailOpenUnlessOpened(new CentrifugeException(CentrifugeErrorCodes.TransportClosed,
                    $"HTTP stream closed before opening: {code} {reason}", true))) return;
            _isOpen = false;
            Closed?.Invoke(this, new TransportClosedEventArgs(code, reason));
            _ = CleanupAsync();
        }

        /// <summary>
        /// Releases the transport's resources. Each one is swapped out and released only once, so
        /// a second cleanup can still release what OpenAsync created after the first (Dispose during
        /// open). The stream is closed first unless <paramref name="alreadyClosed"/> is set. Callbacks
        /// that arrive after the first cleanup are ignored.
        /// </summary>
        private async Task CleanupAsync(bool alreadyClosed = false)
        {
            System.Threading.Interlocked.Exchange(ref _cleanupStarted, 1);
            _isOpen = false;
            _openTcs?.TrySetCanceled();

            lock (_bufferLock)
            {
                _chunkBuffer?.Dispose();
                _chunkBuffer = new MemoryStream();
            }

            var streamId = System.Threading.Interlocked.Exchange(ref _streamId, 0);
            if (streamId > 0)
            {
                if (!alreadyClosed)
                {
                    try
                    {
                        _logger?.LogDebug($"Closing stream {streamId}");
                        await _jsRuntime.InvokeVoidAsync("CentrifugeHttpStream.close", streamId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Error closing stream: {ex.Message}");
                    }
                }

                try
                {
                    await _jsRuntime.InvokeVoidAsync("CentrifugeHttpStream.dispose", streamId).ConfigureAwait(false);
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
                throw new ObjectDisposedException(nameof(BrowserHttpStreamTransport));
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
