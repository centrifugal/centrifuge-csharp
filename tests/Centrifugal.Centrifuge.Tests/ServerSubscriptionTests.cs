using System;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for server-side subscriptions (the ones carried by the connect reply's
    /// subs map, not created with NewSubscription). Behavior is observed over the wire
    /// against the in-process <see cref="FakeCentrifugoServer"/>.
    /// </summary>
    [Collection("Integration")]
    public class ServerSubscriptionTests : IAsyncLifetime
    {
        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public async Task InitializeAsync()
        {
            await _server.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        private static System.Threading.Channels.Channel<T> NewChannel<T>() =>
            System.Threading.Channels.Channel.CreateUnbounded<T>();

        [Fact]
        public async Task ConnectReplyWithoutSubsUnsubscribesPreviousServerSubs()
        {
            // First connect: server hands the client one recoverable server-side sub.
            _server.ConnectResult = new ConnectResult
            {
                Client = "fake-client",
                Version = "0.0.0",
                Ping = 25,
                Subs =
                {
                    ["srv"] = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e1", Offset = 7 }
                }
            };

            var subscribed = NewChannel<CentrifugeServerSubscribedEventArgs>();
            var unsubscribed = NewChannel<CentrifugeServerUnsubscribedEventArgs>();

            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
            _client.ServerSubscribed += (_, e) => subscribed.Writer.TryWrite(e);
            _client.ServerUnsubscribed += (_, e) => unsubscribed.Writer.TryWrite(e);
            _client.Connect();
            await _client.ReadyAsync();

            var first = await subscribed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("srv", first.Channel);

            // The connection drops and the connect reply now carries NO server subs —
            // the user is no longer subscribed to "srv" server-side.
            _server.ConnectResult = new ConnectResult { Client = "fake-client", Version = "0.0.0", Ping = 25 };
            _server.CloseConnection();

            var gone = await unsubscribed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("srv", gone.Channel);

            // ...and the sub must be forgotten, so a further reconnect no longer asks
            // the server to recover a channel the connection has nothing to do with.
            var connectsBefore = _server.Received.Count(c => c.Connect != null);
            _server.CloseConnection();
            await WaitUntilAsync(
                () => _server.Received.Count(c => c.Connect != null) > connectsBefore,
                TimeSpan.FromSeconds(10));

            var lastConnect = _server.Received.Last(c => c.Connect != null).Connect;
            Assert.DoesNotContain("srv", lastConnect.Subs.Keys);
        }

        [Fact]
        public async Task ConnectReplyWithSubsKeepsRecoveryPositionAcrossReconnect()
        {
            // Sanity companion to the test above: while the server keeps returning the
            // sub, the client must keep recovering it from the tracked position.
            _server.ConnectResult = new ConnectResult
            {
                Client = "fake-client",
                Version = "0.0.0",
                Ping = 25,
                Subs =
                {
                    ["srv"] = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e1", Offset = 7 }
                }
            };

            var subscribed = NewChannel<CentrifugeServerSubscribedEventArgs>();
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
            _client.ServerSubscribed += (_, e) => subscribed.Writer.TryWrite(e);
            _client.Connect();
            await _client.ReadyAsync();
            await subscribed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var connectsBefore = _server.Received.Count(c => c.Connect != null);
            _server.CloseConnection();
            await WaitUntilAsync(
                () => _server.Received.Count(c => c.Connect != null) > connectsBefore,
                TimeSpan.FromSeconds(10));

            var lastConnect = _server.Received.Last(c => c.Connect != null).Connect;
            Assert.True(lastConnect.Subs.TryGetValue("srv", out var recover));
            Assert.True(recover!.Recover);
            Assert.Equal(7UL, recover.Offset);
            Assert.Equal("e1", recover.Epoch);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(25);
            }
            throw new TimeoutException("condition not met in time");
        }
    }
}
