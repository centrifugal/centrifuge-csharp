using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// HTTP streaming transport for Centrifugo.
    /// Uses HTTP chunked transfer encoding for server-to-client streaming.
    /// Commands are sent via separate HTTP POST requests.
    /// </summary>
    internal class HttpStreamTransport : ITransport
    {
        private readonly string _endpoint;
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _disposed;
        private bool _isOpen;

        /// <inheritdoc/>
        public TransportType Type => TransportType.HttpStream;

        /// <inheritdoc/>
        public string Name => "http_stream";

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
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <inheritdoc/>
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            if (_isOpen)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            try
            {
                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

                // Wait a bit to ensure connection is established
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                _isOpen = true;
                Opened?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(ErrorCodes.TransportClosed, "Failed to open HTTP stream connection", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (!_isOpen)
            {
                throw new CentrifugeException(ErrorCodes.TransportClosed, "HTTP stream transport is not open");
            }

            try
            {
                // Send command via HTTP POST
                using var content = new ByteArrayContent(data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(ErrorCodes.TransportWriteError, "Failed to send data via HTTP stream", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task CloseAsync()
        {
            if (!_isOpen) return;

            try
            {
                _receiveCts?.Cancel();

                if (_receiveTask != null)
                {
                    await _receiveTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors during close
            }
            finally
            {
                _isOpen = false;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[] tempBuffer = new byte[8192];

                // Read varint-delimited messages from the stream
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[]? message = VarintCodec.ReadDelimitedMessage(stream, tempBuffer, cancellationToken);
                    if (message == null) break;

                    // Dispatch message on thread pool to avoid blocking receive loop
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
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
                Closed?.Invoke(this, new TransportClosedEventArgs(exception: ex));
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
        }
    }
}
