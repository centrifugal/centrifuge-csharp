using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// An exception in the SDK's own processing, on its event loop (an SDK bug or a broken environment), is
    /// reported through Error with type "internal", and the client disconnects with BadProtocol and doesn't
    /// reconnect. Before, it was only logged (with a logger set), so a client that couldn't encode its
    /// connect command, as with a dependency missing at run time in Unity, only timed out connecting, over
    /// and over.
    /// </summary>
    [Collection("Integration")]
    public class InternalErrorTests : IAsyncLifetime
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public Task InitializeAsync() => _server.StartAsync();

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        private CentrifugeClient NewClient()
        {
            var options = new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
                Timeout = TimeSpan.FromSeconds(1),
            };
            return _client = new CentrifugeClient(_server.Url, options);
        }

        private sealed class Observed
        {
            public readonly TaskCompletionSource<CentrifugeErrorEventArgs> InternalError = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<CentrifugeDisconnectedEventArgs> Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Connecting;
            public int InternalErrors;
        }

        private static Observed Observe(CentrifugeClient client)
        {
            var observed = new Observed();
            client.Error += (_, e) =>
            {
                if (e.Type != "internal") return;
                Interlocked.Increment(ref observed.InternalErrors);
                observed.InternalError.TrySetResult(e);
            };
            client.Disconnected += (_, e) => observed.Disconnected.TrySetResult(e);
            client.Connecting += (_, _) => Interlocked.Increment(ref observed.Connecting);
            return observed;
        }

        private static FileNotFoundException MissingAssembly() =>
            new("Could not load file or assembly 'System.Runtime.CompilerServices.Unsafe, Version=4.0.4.1'");

        [Fact]
        public async Task ConnectCommandThatCannotBeEncodedStopsTheClient()
        {
            var client = NewClient();
            var observed = Observe(client);
            client.EncodeCommand = _ => throw MissingAssembly();

            client.Connect();

            var error = await observed.InternalError.Task.WaitAsync(Wait);
            Assert.IsType<FileNotFoundException>(error.Exception);
            var disconnected = await observed.Disconnected.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeDisconnectedCodes.BadProtocol, disconnected.Code);
            Assert.Contains("Unsafe", disconnected.Reason);

            // No reconnect: a permanent fault must not loop, and it is reported once.
            await Task.Delay(1500);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Equal(1, Volatile.Read(ref observed.Connecting));
            Assert.Equal(1, Volatile.Read(ref observed.InternalErrors));
            Assert.DoesNotContain(_server.Received, c => c.Connect != null);
        }

        [Fact]
        public async Task CommandsThatCannotBeEncodedFailInsteadOfHanging()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var observed = Observe(client);

            client.EncodeCommand = _ => throw MissingAssembly();
            var send = client.SendAsync(Encoding.UTF8.GetBytes("{}"));
            var publish = client.PublishAsync("channel", Encoding.UTF8.GetBytes("{}"));

            var sendError = await Assert.ThrowsAsync<CentrifugeException>(() => send.WaitAsync(Wait));
            Assert.Equal(CentrifugeErrorCodes.TransportWriteError, sendError.Code);
            Assert.IsType<FileNotFoundException>(sendError.InnerException);
            var publishError = await Assert.ThrowsAsync<CentrifugeException>(() => publish.WaitAsync(Wait));
            Assert.Equal(CentrifugeErrorCodes.TransportWriteError, publishError.Code);
            Assert.IsType<FileNotFoundException>(publishError.InnerException);
            Assert.IsType<FileNotFoundException>((await observed.InternalError.Task.WaitAsync(Wait)).Exception);
            Assert.Equal(CentrifugeDisconnectedCodes.BadProtocol, (await observed.Disconnected.Task.WaitAsync(Wait)).Code);
        }

        [Fact]
        public async Task ThrowingInternalStepStopsTheClientUntilConnectIsCalled()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var observed = Observe(client);

            client.Loop.Execute(() => throw new InvalidOperationException("sdk bug"));

            Assert.Equal("sdk bug", (await observed.InternalError.Task.WaitAsync(Wait)).Exception?.Message);
            Assert.Equal(CentrifugeDisconnectedCodes.BadProtocol, (await observed.Disconnected.Task.WaitAsync(Wait)).Code);
            await Task.Delay(200);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);

            client.Connect();
            await client.ReadyAsync(Wait);
        }
    }
}
