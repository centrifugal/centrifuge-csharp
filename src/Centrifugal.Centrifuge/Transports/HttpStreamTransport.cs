using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// HTTP streaming transport for Centrifugo.
    /// Uses HTTP chunked transfer encoding for server-to-client streaming.
    /// Commands are sent via separate HTTP POST requests to emulation endpoint.
    /// </summary>
    internal class HttpStreamTransport : ITransport
    {
        private readonly string _endpoint;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly object _openCloseLock = new object();
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private int _disposed;
        private bool _isOpen;

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
        /// Initializes a new instance of the <see cref="HttpStreamTransport"/> class.
        /// </summary>
        /// <param name="endpoint">HTTP stream endpoint URL.</param>
        /// <param name="httpClient">HTTP client to use (optional, will create new if not provided).</param>
        public HttpStreamTransport(string endpoint, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            _endpoint = endpoint;
            if (httpClient == null)
            {
                _httpClient = new HttpClient();
                // Configure for streaming - no timeout on the connection itself
                _httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                _ownsHttpClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
        }

        /// <inheritdoc/>
        public async Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null)
        {
            if (_isOpen)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            CancellationTokenSource? receiveCts = null;
            try
            {
                var openedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_openCloseLock)
                {
                    _receiveCts = new CancellationTokenSource();
                    receiveCts = _receiveCts;
                }
                _receiveTask = ReceiveLoopAsync(initialData ?? Array.Empty<byte>(), openedTcs, receiveCts.Token);

                // Wait for the connection to open or timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                var completedTask = await Task.WhenAny(openedTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
                if (completedTask != openedTcs.Task)
                {
                    throw new TimeoutException("Timeout waiting for HTTP stream connection to open");
                }

                // Propagate any exception the receive loop set on openedTcs (early HTTP failure, etc.)
                await openedTcs.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                receiveCts?.Cancel();
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "Failed to open HTTP stream connection", true, ex);
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
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "HTTP stream transport is disposed");
            }
            bool isOpen;
            lock (_openCloseLock) { isOpen = _isOpen; }
            if (!isOpen)
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
                using var content = new ByteArrayContent(requestBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                using var response = await _httpClient.PostAsync(emulationEndpoint, content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "Failed to send data via HTTP stream", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task CloseAsync()
        {
            CancellationTokenSource? cts;
            lock (_openCloseLock)
            {
                if (!_isOpen && _receiveCts == null) return;
                cts = _receiveCts;
                _isOpen = false;
            }

            try
            {
                cts?.Cancel();

                if (_receiveTask != null)
                {
                    await _receiveTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors during close
            }
        }

        private async Task ReceiveLoopAsync(byte[] initialData, TaskCompletionSource<bool> openedTcs, CancellationToken cancellationToken)
        {
            bool connectionOpened = false;
            try
            {
                // Send initial POST request with varint-delimited connect command
                using var ms = new MemoryStream();
                VarintCodec.WriteDelimitedMessage(ms, initialData);
                using var content = new ByteArrayContent(ms.ToArray());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = content
                };
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
                request.Headers.ConnectionClose = false; // Keep connection alive for streaming

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // Non-2xx response: propagate via openedTcs so OpenAsync throws and the normal
                // reconnect path handles retrying.  Do NOT fire Closed — OnTransportClosed is
                // already registered and firing it would cause a duplicate reconnect.
                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    openedTcs.TrySetException(new CentrifugeException(CentrifugeErrorCodes.TransportClosed, $"http error {statusCode}", true));
                    return;
                }

                connectionOpened = true;
                lock (_openCloseLock) { _isOpen = true; }
                openedTcs.TrySetResult(true);
                Opened?.Invoke(this, EventArgs.Empty);

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[] tempBuffer = new byte[8192];

                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[]? message = await VarintCodec.ReadDelimitedMessageAsync(stream, tempBuffer, cancellationToken).ConfigureAwait(false);
                    if (message == null) break;
                    MessageReceived?.Invoke(this, message);
                }

                // Server closed the stream (EOF) — fire Closed so the client can reconnect
                if (!cancellationToken.IsCancellationRequested)
                {
                    Closed?.Invoke(this, new TransportClosedEventArgs());
                }
            }
            catch (OperationCanceledException)
            {
                openedTcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                if (connectionOpened)
                {
                    // Post-open failure: inform the client the transport died
                    Error?.Invoke(this, ex);
                    Closed?.Invoke(this, new TransportClosedEventArgs(exception: ex));
                }
                else
                {
                    // Pre-open failure: propagate via openedTcs so OpenAsync throws;
                    // the caller's reconnect path handles retrying without a duplicate Closed
                    openedTcs.TrySetException(ex);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            CancellationTokenSource? cts;
            lock (_openCloseLock)
            {
                cts = _receiveCts;
                _isOpen = false;
            }
            cts?.Cancel();
            cts?.Dispose();

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
