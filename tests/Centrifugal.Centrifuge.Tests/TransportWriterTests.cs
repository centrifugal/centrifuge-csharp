using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// The writer of a transport: frames go out one at a time, in order; an emulation request that hangs fails
    /// after the deadline instead of holding back every later command and pong.
    /// </summary>
    public class TransportWriterTests
    {
        private sealed class FakeEmulationTransport : ITransport
        {
            /// <summary>Requests whose first byte is in here hang until cancelled.</summary>
            public readonly HashSet<byte> Hang = new();
            public readonly ConcurrentQueue<byte> Sent = new();

            public CentrifugeTransportType Type => CentrifugeTransportType.HttpStream;
            public string Name => "fake";
            public bool UsesEmulation => true;
#pragma warning disable CS0067
            public event EventHandler? Opened;
            public event EventHandler<IReadOnlyList<byte[]>>? MessageReceived;
            public event EventHandler<TransportClosedEventArgs>? Closed;
            public event EventHandler<Exception>? Error;
#pragma warning restore CS0067
            public Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null) => Task.CompletedTask;
            public Task SendAsync(byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public async Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default)
            {
                if (Hang.Contains(data[0])) await Task.Delay(Timeout.Infinite, cancellationToken);
                Sent.Enqueue(data[0]);
            }

            public Task CloseAsync() => Task.CompletedTask;
            public void Dispose() { }
        }

        [Fact]
        public async Task HungEmulationRequestFailsAfterDeadlineAndLaterFramesGoOut()
        {
            var transport = new FakeEmulationTransport();
            transport.Hang.Add(1);
            var results = new ConcurrentQueue<(byte, Exception?)>();
            var writer = new TransportWriter(transport, (frame, error) => results.Enqueue((frame.Data[0], error)), TimeSpan.FromMilliseconds(300));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            writer.Enqueue(new OutgoingFrame(new byte[] { 1 }, null, null));
            writer.Enqueue(new OutgoingFrame(new byte[] { 2 }, null, null));
            Assert.True(SpinWait.SpinUntil(() => results.Count == 2, TimeSpan.FromSeconds(5)), "the writer is stuck behind the hung request");

            var outcomes = results.ToArray();
            Assert.Equal(1, outcomes[0].Item1);
            Assert.IsAssignableFrom<OperationCanceledException>(outcomes[0].Item2);
            Assert.Equal(2, outcomes[1].Item1);
            Assert.Null(outcomes[1].Item2);
            Assert.True(sw.ElapsedMilliseconds < 3000);
            Assert.Equal(new byte[] { 2 }, transport.Sent);
            // The failure of the hung request closes an emulation transport (outcome unknown).
            Assert.True(CentrifugeClient.EmulationTransportFailed(new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "w", true, outcomes[0].Item2)));
            await Task.CompletedTask;
        }

        [Fact]
        public void DeadlineCancelsOnTimeWhenNotLate()
        {
            using var deadline = new LateGraceDeadline(TimeSpan.FromMilliseconds(100));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(deadline.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            Assert.InRange(sw.ElapsedMilliseconds, 50, 900);
        }

        /// <summary>
        /// A deadline timer running far too late (the process was suspended) gives the work a grace period
        /// before cancelling: a response already waiting in the socket is read first (pattern 28/55, X1).
        /// </summary>
        [Fact]
        public void LateDeadlineGivesGraceBeforeCancelling()
        {
            var clock = 0L;
            using var deadline = new LateGraceDeadline(TimeSpan.FromMilliseconds(100), () => Interlocked.Read(ref clock));
            // Simulate a suspension: when the timer fires, the clock is far past the due time.
            Interlocked.Exchange(ref clock, 100 + LateGraceDeadline.LateMs + 5000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(deadline.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            Assert.InRange(sw.ElapsedMilliseconds, 100 + LateGraceDeadline.GraceMs - 200, 4000);
        }

        [Fact]
        public void FramesGoOutInOrderOneAtATime()
        {
            var transport = new FakeEmulationTransport();
            var results = new ConcurrentQueue<byte>();
            var writer = new TransportWriter(transport, (frame, _) => results.Enqueue(frame.Data[0]), TimeSpan.FromSeconds(5));
            for (byte i = 10; i < 60; i++) writer.Enqueue(new OutgoingFrame(new[] { i }, null, null));
            Assert.True(SpinWait.SpinUntil(() => results.Count == 50, TimeSpan.FromSeconds(5)));
            Assert.Equal(System.Linq.Enumerable.Range(10, 50), System.Linq.Enumerable.Select(transport.Sent, b => (int)b));
        }

        [Fact]
        public void ClosedWriterReturnsUnwrittenFramesAndRefusesNew()
        {
            var transport = new FakeEmulationTransport();
            transport.Hang.Add(1);
            var writer = new TransportWriter(transport, (_, _) => { }, TimeSpan.FromSeconds(5));
            writer.Enqueue(new OutgoingFrame(new byte[] { 1 }, null, null));
            writer.Enqueue(new OutgoingFrame(new byte[] { 2 }, null, null));
            Thread.Sleep(100);
            var dropped = writer.Close();
            Assert.Single(dropped);
            Assert.Equal(2, dropped[0].Data[0]);
            Assert.False(writer.Enqueue(new OutgoingFrame(new byte[] { 3 }, null, null)));
        }
    }
}
