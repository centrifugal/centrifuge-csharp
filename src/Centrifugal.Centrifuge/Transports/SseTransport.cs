using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// Server-Sent Events (SSE) transport for Centrifugo.
    /// SSE is unidirectional (server to client), so commands are sent via HTTP POST.
    /// </summary>
    internal class SseTransport : ITransport
    {
        private readonly string _endpoint;
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _disposed;
        private bool _isOpen;

        /// <inheritdoc/>
        public TransportType Type => TransportType.SSE;

        /// <inheritdoc/>
        public string Name => "sse";

        /// <inheritdoc/>
        public event EventHandler? Opened;

        /// <inheritdoc/>
        public event EventHandler<byte[]>? MessageReceived;

        /// <inheritdoc/>
        public event EventHandler<TransportClosedEventArgs>? Closed;

        /// <inheritdoc/>
        public event EventHandler<Exception>? Error;

        /// <summary>
        /// Initializes a new instance of the <see cref="SseTransport"/> class.
        /// </summary>
        /// <param name="endpoint">SSE endpoint URL.</param>
        /// <param name="httpClient">HTTP client to use (optional, will create new if not provided).</param>
        public SseTransport(string endpoint, HttpClient? httpClient = null)
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
                throw new CentrifugeException(ErrorCodes.TransportClosed, "Failed to open SSE connection", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (!_isOpen)
            {
                throw new CentrifugeException(ErrorCodes.TransportClosed, "SSE transport is not open");
            }

            try
            {
                // SSE is unidirectional, so we send commands via HTTP POST
                using var content = new ByteArrayContent(data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new CentrifugeException(ErrorCodes.TransportWriteError, "Failed to send data via SSE", true, ex);
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
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var dataBuilder = new StringBuilder();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    if (string.IsNullOrEmpty(line))
                    {
                        // Empty line indicates end of event
                        if (dataBuilder.Length > 0)
                        {
                            var data = dataBuilder.ToString();
                            dataBuilder.Clear();

                            // Parse and dispatch message
                            ProcessSseData(data);
                        }
                        continue;
                    }

                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        dataBuilder.AppendLine(line.Substring(6));
                    }
                    // Ignore comment lines starting with ':'
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

        private void ProcessSseData(string data)
        {
            try
            {
                // SSE data is base64 encoded Protobuf
                byte[] messageData = Convert.FromBase64String(data.Trim());

                _ = Task.Run(() =>
                {
                    try
                    {
                        MessageReceived?.Invoke(this, messageData);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, ex);
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
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
