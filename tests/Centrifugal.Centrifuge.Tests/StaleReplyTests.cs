using System;
using System.Text;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for the stale subscribe reply guard: a reply produced by a connection
    /// that was torn down between the reply arriving and being processed must be
    /// discarded, otherwise it would flip the subscription to Subscribed while the
    /// client reconnects — and the post-reconnect resubscribe sweep would skip it,
    /// stranding the subscription without a server-side counterpart.
    /// </summary>
    [Collection("Integration")]
    public class StaleReplyTests : IAsyncLifetime
    {
        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public async Task InitializeAsync()
        {
            // Negotiate channel compaction so the post-reconnect resubscribe gets a
            // numeric channel id (42) we can route pushes to in the assertions below.
            _server.OnSubscribe = (channel, req) =>
                new SubscribeResult { Id = (req.Flag & 1) != 0 ? 42 : 0 };
            await _server.StartAsync();
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            });
        }

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        [Fact]
        public async Task ConnectionGenerationBumpedOnTeardownAndStaleReplyDiscarded()
        {
            var subscribedEvents = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeSubscribedEventArgs>();
            var pubs = System.Threading.Channels.Channel.CreateUnbounded<CentrifugePublicationEventArgs>();

            _client!.Connect();
            await _client.ReadyAsync();

            var sub = _client.NewSubscription("stale-reply-channel");
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (_, e) => pubs.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var generationBeforeTeardown = _client.ConnectionGeneration;

            // Real teardown: leaves Connected → generation must be bumped. The client
            // then reconnects and the subscription resubscribes automatically.
            await _client.HandleNoPingAsync();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(_client.ConnectionGeneration > generationBeforeTeardown,
                "leaving Connected must bump the connection generation");

            // Mechanism check: a reply stamped with the pre-teardown generation is
            // discarded — it must not apply any state (the compaction ID registry
            // gives an observable side effect to assert on).
            var staleResult = new SubscribeResult { Id = 77 };
            Assert.False(sub.HandleSubscribeReply(staleResult, generationBeforeTeardown),
                "reply from a torn-down connection must be discarded");

            // ID 77 was not registered; ID 42 (registered by the legitimate
            // post-reconnect resubscribe) still routes. Ordered socket: when the
            // second push arrives, the first was already processed and dropped.
            await _server.PublishIdAsync(77, Encoding.UTF8.GetBytes("{\"stale\":true}"));
            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"ok\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "stale reply must not have registered its channel ID");

            // The same reply stamped with the current generation applies normally.
            Assert.True(sub.HandleSubscribeReply(staleResult, _client.ConnectionGeneration));
            await _server.PublishIdAsync(77, Encoding.UTF8.GetBytes("{\"applied\":true}"));
            pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"applied\":true}", Encoding.UTF8.GetString(pub.Data.Span));
        }
    }
}
