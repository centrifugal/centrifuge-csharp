using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
        private int _disposed;
        /// <summary>Stopwatch timestamp of the first failed write, or 0. Once it is set every later write
        /// fails immediately, so no data goes out after data that was lost.</summary>
        private long _writeFailedAt;
        /// <summary>Sequence number of the receive loop's current read, and a flag set only while that read
        /// waits on the network. Only that waiting time counts toward the grace after a failed write.</summary>
        private long _readSeq;
        private int _reading;

        /// <summary>Network wait the receive loop is given after a failed write to pick up the server's
        /// close, before the socket is aborted.</summary>
        private static readonly TimeSpan FailedWriteCloseGrace = TimeSpan.FromSeconds(1);

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
        /// <remarks>Fails on a disposed transport. The socket and the receive token source are stored
        /// with Interlocked (a full fence) before checking for disposal, so a racing Dispose either
        /// releases them itself or is noticed here, before connecting or before the receive loop
        /// starts.</remarks>
        public async Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null)
        {
            if (_webSocket != null)
            {
                throw new InvalidOperationException("Transport is already open");
            }

            // WebSocket doesn't use initialData parameter
            var webSocket = new ClientWebSocket();
            webSocket.Options.AddSubProtocol(_subProtocol);
            var receiveCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _receiveCts, receiveCts);
            Interlocked.Exchange(ref _webSocket, webSocket);
            if (Volatile.Read(ref _disposed) != 0)
            {
                webSocket.Dispose();
                receiveCts.Dispose();
                throw new ObjectDisposedException(nameof(WebSocketTransport));
            }

            try
            {
                await webSocket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(WebSocketTransport));

                _receiveTask = ReceiveLoopAsync(receiveCts.Token);

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
        /// <remarks>If <paramref name="cancellationToken"/> fires before the write completes, the socket is
        /// aborted. After a write error, later writes fail immediately, while the receive loop gets up to
        /// <see cref="FailedWriteCloseGrace"/> of network wait in total to read the server's close, which may
        /// already be buffered. The read pending at that moment is aborted when the grace runs out; later
        /// reads get whatever remains. Dispatch time is not counted. In both cases Closed comes from the
        /// receive loop, exactly once.</remarks>
        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            bool locked = false;
            var webSocket = _webSocket;
            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                locked = true;
                if (webSocket == null || webSocket.State != WebSocketState.Open || Interlocked.Read(ref _writeFailedAt) != 0)
                {
                    throw new CentrifugeException(CentrifugeErrorCodes.TransportClosed, "WebSocket is not open");
                }

                await webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                webSocket?.Abort();
                throw;
            }
            catch (Exception) when (webSocket != null)
            {
                if (Interlocked.CompareExchange(ref _writeFailedAt, Stopwatch.GetTimestamp(), 0) == 0
                    && Volatile.Read(ref _reading) != 0)
                    _ = AbortReadAfterGraceAsync(webSocket, Interlocked.Read(ref _readSeq));
                throw;
            }
            finally
            {
                if (locked) _sendLock.Release();
            }
        }

        /// <summary>Waits out the grace, then aborts the socket if read <paramref name="readSeq"/> (pending
        /// when the write failed) is still waiting on the network.</summary>
        private async Task AbortReadAfterGraceAsync(ClientWebSocket webSocket, long readSeq)
        {
            await Task.Delay(FailedWriteCloseGrace).ConfigureAwait(false);
            if (Volatile.Read(ref _reading) != 0 && Interlocked.Read(ref _readSeq) == readSeq) webSocket.Abort();
        }

        /// <inheritdoc/>
        public Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("WebSocket transport does not use emulation mode");
        }

        /// <inheritdoc/>
        /// <remarks>Sends the close frame first, under the send lock and within 5 seconds: cancelling a
        /// pending receive aborts the socket, and no close frame can be sent after that.
        /// CloseOutputAsync doesn't wait for the server's acknowledgement, so a receive running at the
        /// same time is fine. Only then is the receive loop cancelled and awaited. Errors while closing
        /// are ignored since the connection goes away regardless.</remarks>
        public async Task CloseAsync()
        {
            var webSocket = _webSocket;
            if (webSocket == null) return;
            var cts = _receiveCts;
            var receiveTask = _receiveTask;

            using (var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                bool locked = false;
                try
                {
                    await _sendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
                    locked = true;
                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", closeCts.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                }
                finally
                {
                    if (locked) _sendLock.Release();
                }
            }

            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            if (receiveTask != null)
            {
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Receive loop that processes incoming messages. Every WebSocket message becomes one frame,
        /// raised in order. If it holds a malformed message, the valid ones before it are still raised
        /// and the parse error then closes the transport. Cancellation is a normal stop only when it
        /// comes from the loop's own token; any other cancellation means the transport failed.
        ///
        /// After each message the buffer shrinks back to ReceiveBufferSize, so a large message doesn't
        /// stay allocated (the next large one grows it again). Once a write has failed, reads are bounded
        /// by <see cref="FailedWriteCloseGrace"/> in total (see SendAsync).
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var ms = new MemoryStream();
            var frame = new List<byte[]>();
            // Capture the socket as a local so a concurrent Dispose nulling _webSocket
            // (future-proofing) can't cause NREs mid-loop.
            var webSocket = _webSocket;
            var graceLeft = FailedWriteCloseGrace;

            try
            {
                while (!cancellationToken.IsCancellationRequested && webSocket != null)
                {
                    WebSocketReceiveResult result;
                    ms.SetLength(0);
                    if (ms.Capacity > VarintCodec.ReceiveBufferSize) ms.Capacity = VarintCodec.ReceiveBufferSize;

                    // Read the complete WebSocket message
                    do
                    {
                        Interlocked.Increment(ref _readSeq);
                        Interlocked.Exchange(ref _reading, 1);
                        var readStart = Stopwatch.GetTimestamp();
                        try
                        {
                            if (Interlocked.Read(ref _writeFailedAt) == 0)
                            {
                                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                grace.CancelAfter(graceLeft > TimeSpan.Zero ? graceLeft : TimeSpan.Zero);
                                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), grace.Token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref _reading, 0);
                        }
                        var failedAt = Interlocked.Read(ref _writeFailedAt);
                        if (failedAt != 0)
                        {
                            graceLeft -= TimeSpan.FromSeconds(
                                (Stopwatch.GetTimestamp() - Math.Max(readStart, failedAt)) / (double)Stopwatch.Frequency);
                        }

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

                    try
                    {
                        var length = (int)ms.Length;
                        if (VarintCodec.ReadCompleteMessages(ms.GetBuffer(), length, frame) != length)
                            throw new IOException("Truncated message in WebSocket frame");
                    }
                    finally
                    {
                        if (frame.Count > 0) MessageReceived?.Invoke(this, frame);
                        frame.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
                Closed?.Invoke(this, new TransportClosedEventArgs());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ms.Dispose();
            }
        }

        /// <inheritdoc/>
        /// <remarks>The token source may already be disposed by an OpenAsync that noticed the disposal.
        /// The send lock isn't disposed, so a sender waiting on it still acquires it and then fails on
        /// the closed socket.</remarks>
        public void Dispose()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            try { _receiveCts?.Cancel(); } catch (ObjectDisposedException) { }
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
        }
    }
}
