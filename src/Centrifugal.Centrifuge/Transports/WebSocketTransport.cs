using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// WebSocket transport for Centrifugo.
    /// </summary>
    internal class WebSocketTransport : ITransport
    {
        private readonly Uri _uri;
        private readonly string _subProtocol;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

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
        /// Initializes a new instance of the <see cref="WebSocketTransport"/> class.
        /// </summary>
        /// <param name="endpoint">WebSocket endpoint URL.</param>
        public WebSocketTransport(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            _uri = new Uri(endpoint);
            _subProtocol = "centrifuge-protobuf";
        }

        /// <inheritdoc/>
        public async Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null)
        {
            if (_webSocket != null)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            // WebSocket doesn't use initialData parameter
            _webSocket = new ClientWebSocket();
            _webSocket.Options.AddSubProtocol(_subProtocol);

            try
            {
                await _webSocket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);

                // Start receive loop
                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

                Opened?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _webSocket?.Dispose();
                _webSocket = null;
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "Failed to connect WebSocket", true, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "WebSocket is not open");
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
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
            if (_webSocket == null) return;

            try
            {
                _receiveCts?.Cancel();

                if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", cts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors during close
            }
            finally
            {
                if (_receiveTask != null)
                {
                    try
                    {
                        await _receiveTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore errors during receive task completion
                    }
                }
            }
        }

        /// <summary>
        /// Receive loop that processes incoming messages.
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var ms = new MemoryStream();

            try
            {
                while (!cancellationToken.IsCancellationRequested && _webSocket != null)
                {
                    WebSocketReceiveResult result;
                    ms.SetLength(0);

                    // Read the complete WebSocket message
                    do
                    {
                        result = await _webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken
                        ).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Closed?.Invoke(this, new TransportClosedEventArgs(
                                (int?)result.CloseStatus,
                                result.CloseStatusDescription
                            ));
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    // Process varint-delimited messages within the WebSocket message
                    ms.Position = 0;
                    byte[] tempBuffer = new byte[8192];

                    while (ms.Position < ms.Length)
                    {
                        byte[]? message = VarintCodec.ReadDelimitedMessage(ms, tempBuffer, cancellationToken);
                        if (message == null) break;

                        // Invoke synchronously to preserve message order
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
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ms.Dispose();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _sendLock?.Dispose();
            _webSocket?.Dispose();
        }
    }
}
