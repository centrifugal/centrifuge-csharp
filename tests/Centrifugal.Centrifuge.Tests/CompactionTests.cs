using System;
using System.Text;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for channel compaction. The feature is Centrifugo PRO only, so it
    /// can't be exercised against the docker-compose OSS server — these tests use
    /// the in-process <see cref="FakeCentrifugoServer"/>: the subscribe reply
    /// negotiates a numeric channel ID and subsequent pushes carry the ID instead
    /// of the channel name, exactly like the real server does when compaction is
    /// enabled.
    /// </summary>
    [Collection("Integration")]
    public class CompactionTests : IAsyncLifetime
    {
        private readonly FakeCentrifugoServer _server = new();
        // Numeric channel id assigned on the next subscribe; a test can change it
        // before a resubscribe to exercise id refresh.
        private long _nextChannelId = 42;
        private CentrifugeClient? _client;

        public async Task InitializeAsync()
        {
            // Negotiate channel compaction: assign a numeric channel id whenever the
            // client offers the channelCompaction flag (bit 1).
            _server.OnSubscribe = (channel, req) =>
                new SubscribeResult { Id = (req.Flag & 1) != 0 ? _nextChannelId : 0 };
            await _server.StartAsync();
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
        }

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        private async Task<CentrifugeSubscription> ConnectAndSubscribeAsync(
            System.Threading.Channels.Channel<CentrifugePublicationEventArgs> pubs,
            System.Threading.Channels.Channel<CentrifugeSubscribedEventArgs> subscribedEvents)
        {
            _client!.Connect();
            await _client.ReadyAsync();

            var sub = _client.NewSubscription("compacted-channel");
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (_, e) => pubs.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return sub;
        }

        private static System.Threading.Channels.Channel<T> NewChannel<T>() =>
            System.Threading.Channels.Channel.CreateUnbounded<T>();

        [Fact]
        public async Task SubscribeOffersCompactionFlagAndPushesRoutedByNumericId()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            var joins = NewChannel<CentrifugeJoinEventArgs>();
            var leaves = NewChannel<CentrifugeLeaveEventArgs>();
            sub.Join += (_, e) => joins.Writer.TryWrite(e);
            sub.Leave += (_, e) => leaves.Writer.TryWrite(e);

            // The subscribe request must carry the channelCompaction bit.
            Assert.Equal(1, _server.LastSubscribe()!.Flag & 1);

            // Publication push with numeric ID only (no channel name).
            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"compacted\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"compacted\":true}", Encoding.UTF8.GetString(pub.Data.Span));

            // Join / leave pushes are compacted the same way.
            await _server.JoinIdAsync(42, "other-client");
            var join = await joins.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other-client", join.Info.Client);

            await _server.LeaveIdAsync(42, "other-client");
            var leave = await leaves.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other-client", leave.Info.Client);
        }

        [Fact]
        public async Task PushWithUnknownIdIsDroppedWithoutAffectingOthers()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            // Unknown ID — must not be delivered anywhere (and must not crash).
            await _server.PublishIdAsync(99, Encoding.UTF8.GetBytes("{\"stray\":true}"));
            // Known ID delivered after the stray one (ordered same socket) proves
            // the client processed and dropped the stray push.
            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"ok\":true}"));

            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "stray push with unknown id was delivered");
        }

        [Fact]
        public async Task IdMappingDroppedOnUnsubscribeAndRefreshedOnResubscribe()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            var unsubscribedEvents = NewChannel<CentrifugeUnsubscribedEventArgs>();
            sub.Unsubscribed += (_, e) => unsubscribedEvents.Writer.TryWrite(e);
            sub.Unsubscribe();
            await unsubscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // Old ID must no longer route to the unsubscribed subscription.
            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"stale\":true}"));

            // Resubscribe — the server assigns a fresh ID.
            _nextChannelId = 43;
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await _server.PublishIdAsync(43, Encoding.UTF8.GetBytes("{\"fresh\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"fresh\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "stale push delivered after unsubscribe");
        }

        [Fact]
        public async Task SameIdReRegisteredAfterDisconnectReconnect()
        {
            // Regression guard (found in the dart port): the client drops the ID
            // registry on transport teardown (IDs are server-session-scoped), and on
            // reconnect the server commonly assigns the SAME ID to the channel again.
            // The subscription must re-register it even though its own remembered ID
            // is unchanged.
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            _client!.Disconnect();

            _client.Connect();
            // Subscription auto-resubscribes on reconnect — wait for the second
            // Subscribed event. Same ID 42 as before the reconnect.
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"after_reconnect\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"after_reconnect\":true}", Encoding.UTF8.GetString(pub.Data.Span));
        }
    }
}
