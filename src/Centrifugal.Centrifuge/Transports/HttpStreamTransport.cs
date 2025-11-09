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
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _disposed;
        private bool _isOpen;

        /// <inheritdoc/>
        public TransportType Type => TransportType.HttpStream;

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
            }
            else
            {
                _httpClient = httpClient;
            }
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
                var openedTcs = new TaskCompletionSource<bool>();

                EventHandler? openedHandler = null;
                openedHandler = (s, e) =>
                {
                    openedTcs.TrySetResult(true);
                    Opened -= openedHandler;
                };
                Opened += openedHandler;

                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(initialData ?? Array.Empty<byte>(), _receiveCts.Token);

                // Wait for the connection to open or timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                var completedTask = await Task.WhenAny(openedTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
                if (completedTask != openedTcs.Task)
                {
                    throw new TimeoutException("Timeout waiting for HTTP stream connection to open");
                }

                _isOpen = true;
            }
            catch (Exception ex)
            {
                _receiveCts?.Cancel();
                throw new CentrifugeException(ErrorCodes.TransportClosed, "Failed to open HTTP stream connection", true, ex);
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
                throw new CentrifugeException(ErrorCodes.TransportClosed, "HTTP stream transport is not open");
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

                var response = await _httpClient.PostAsync(emulationEndpoint, content, cancellationToken).ConfigureAwait(false);
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

        private async Task ReceiveLoopAsync(byte[] initialData, CancellationToken cancellationToken)
        {
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

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Connection established successfully
                Opened?.Invoke(this, EventArgs.Empty);

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[] tempBuffer = new byte[8192];

                // Read varint-delimited messages from the stream
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[]? message = await VarintCodec.ReadDelimitedMessageAsync(stream, tempBuffer, cancellationToken).ConfigureAwait(false);
                    if (message == null)
                    {
                        break;
                    }

                    // Invoke MessageReceived event synchronously
                    try
                    {
                        MessageReceived?.Invoke(this, message);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, ex);
                    }
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
