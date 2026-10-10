using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;

namespace Centrifugal.Centrifuge
{
    /// <summary>A frame of commands for the transport: the ids of the calls it carries, the completions of
    /// the sends (commands without reply) in it, and where an emulation transport posts it.</summary>
    internal sealed class OutgoingFrame
    {
        public OutgoingFrame(byte[] data, List<uint>? callIds, List<TaskCompletionSource<bool>>? sends)
        {
            Data = data;
            CallIds = callIds;
            Sends = sends;
        }

        public byte[] Data { get; }

        public List<uint>? CallIds { get; }

        public List<TaskCompletionSource<bool>>? Sends { get; }

        public string Session { get; set; } = string.Empty;

        public string Node { get; set; } = string.Empty;

        public string EmulationEndpoint { get; set; } = string.Empty;
    }

    /// <summary>
    /// Writes the frames of one transport in order, one at a time: a WebSocket takes one send at a time, and
    /// an emulation request is posted only after the previous one was answered, so the server handles the
    /// commands in the order they were sent — over emulation too. The outcome of each write is reported to
    /// the client, which applies it on its loop.
    /// </summary>
    internal sealed class TransportWriter
    {
        private readonly ITransport _transport;
        private readonly Action<OutgoingFrame, Exception?> _onWritten;
        private readonly TimeSpan _emulationTimeout;
        private readonly object _lock = new object();
        private readonly Queue<OutgoingFrame> _queue = new Queue<OutgoingFrame>();
        private bool _writing;
        private bool _closed;

        /// <param name="transport">The transport.</param>
        /// <param name="onWritten">Called, off the loop, with each frame written (null) or failed (the error).</param>
        /// <param name="emulationTimeout">How long an emulation request may take: one that hangs (e.g. in an
        /// intermediary) would hold back every later command and pong, so it fails — and the transport closes,
        /// the outcome of its commands being unknown.</param>
        public TransportWriter(ITransport transport, Action<OutgoingFrame, Exception?> onWritten, TimeSpan emulationTimeout)
        {
            _transport = transport;
            _onWritten = onWritten;
            _emulationTimeout = emulationTimeout;
        }

        /// <summary>Queues a frame; false once the writer is closed.</summary>
        public bool Enqueue(OutgoingFrame frame)
        {
            lock (_lock)
            {
                if (_closed) return false;
                _queue.Enqueue(frame);
                if (_writing) return true;
                _writing = true;
            }
            _ = Task.Run(WriteLoopAsync);
            return true;
        }

        /// <summary>Stops writing: returns the frames not written yet. A write in progress completes (a
        /// graceful close waits for it).</summary>
        public List<OutgoingFrame> Close()
        {
            lock (_lock)
            {
                _closed = true;
                var dropped = new List<OutgoingFrame>(_queue);
                _queue.Clear();
                return dropped;
            }
        }

        private async Task WriteLoopAsync()
        {
            while (true)
            {
                OutgoingFrame frame;
                lock (_lock)
                {
                    if (_closed || _queue.Count == 0)
                    {
                        _writing = false;
                        return;
                    }
                    frame = _queue.Dequeue();
                }

                Exception? error = null;
                try
                {
                    if (_transport.UsesEmulation)
                    {
                        using var deadline = new LateGraceDeadline(_emulationTimeout);
                        await _transport.SendEmulationAsync(frame.Data, frame.Session, frame.Node, frame.EmulationEndpoint,
                            deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _transport.SendAsync(frame.Data, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                _onWritten(frame, error);
            }
        }
    }

    /// <summary>
    /// A deadline whose token is cancelled after a timeout — unless the timer runs far too late (the process
    /// was suspended, e.g. a laptop sleep): then a response that arrived meanwhile and waits to be read gets a
    /// grace period first, as the call and no-ping timers of the client do (centrifuge-js lateCallTimer).
    /// </summary>
    internal sealed class LateGraceDeadline : IDisposable
    {
        /// <summary>A timer running at least this late means the process didn't run meanwhile.</summary>
        internal const int LateMs = 1000;
        /// <summary>How long the work still has then.</summary>
        internal const int GraceMs = 1000;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Timer _timer;
        private readonly Func<long> _now;
        private readonly long _due;
        private int _disposed;
        private bool _graced;

        /// <param name="timeout">The timeout.</param>
        /// <param name="now">Monotonic milliseconds (for tests); <see cref="Clock.NowMs"/> by default.</param>
        public LateGraceDeadline(TimeSpan timeout, Func<long>? now = null)
        {
            _now = now ?? (() => Clock.NowMs);
            var timeoutMs = (int)Math.Max(0, timeout.TotalMilliseconds);
            _due = _now() + timeoutMs;
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(timeoutMs, Timeout.Infinite);
        }

        public CancellationToken Token => _cts.Token;

        private void Fire()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_graced && _now() - _due >= LateMs)
            {
                _graced = true;
                try { _timer.Change(GraceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
                return;
            }
            Cancel();
        }

        private void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}
