using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// Server-Sent Events (SSE) transport for Centrifugo.
    /// SSE is unidirectional (server to client), so commands are sent via HTTP POST to emulation endpoint.
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
                    throw new TimeoutException("Timeout waiting for SSE connection to open");
                }

                _isOpen = true;
            }
            catch (Exception ex)
            {
                _receiveCts?.Cancel();
                throw new CentrifugeException(ErrorCodes.TransportClosed, "Failed to open SSE connection", true, ex);
            }
        }

        /// <inheritdoc/>
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("SSE transport uses emulation mode. Use SendEmulationAsync instead.");
        }

        /// <inheritdoc/>
        public async Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default)
        {
            if (!_isOpen)
            {
                throw new CentrifugeException(ErrorCodes.TransportClosed, "SSE transport is not open");
            }

            try
            {
                // SSE uses JSON for emulation requests (like JavaScript client)
                var emulationRequest = new
                {
                    session = session,
                    node = node,
                    data = Convert.ToBase64String(data)
                };

                var json = JsonSerializer.Serialize(emulationRequest);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(emulationEndpoint, content, cancellationToken).ConfigureAwait(false);
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

        private async Task ReceiveLoopAsync(byte[] initialData, CancellationToken cancellationToken)
        {
            try
            {
                // Build URL with cf_connect query parameter
                var uriBuilder = new UriBuilder(_endpoint);

                // SSE uses JSON protocol, so we need to convert protobuf Command to JSON
                string connectDataJson;
                if (initialData.Length > 0)
                {
                    var command = Command.Parser.ParseFrom(initialData);
                    var settings = new JsonFormatter.Settings(false); // Don't format for readability
                    var formatter = new JsonFormatter(settings);
                    connectDataJson = formatter.Format(command);
                }
                else
                {
                    connectDataJson = "{}";
                }

                // Append query parameter
                if (string.IsNullOrEmpty(uriBuilder.Query))
                {
                    uriBuilder.Query = $"cf_connect={Uri.EscapeDataString(connectDataJson)}";
                }
                else
                {
                    uriBuilder.Query = uriBuilder.Query.Substring(1) + $"&cf_connect={Uri.EscapeDataString(connectDataJson)}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Connection established successfully
                Opened?.Invoke(this, EventArgs.Empty);

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
                        dataBuilder.Append(line.Substring(6));
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
                // SSE uses JSON protocol, so parse JSON and convert to protobuf Reply
                var jsonData = data.Trim();
                var parser = new JsonParser(JsonParser.Settings.Default);
                var reply = parser.Parse<Reply>(jsonData);

                // Convert Reply back to protobuf bytes for the message handler
                byte[] messageData = reply.ToByteArray();

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
