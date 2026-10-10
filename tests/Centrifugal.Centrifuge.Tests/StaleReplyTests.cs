using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// A subscribe reply of a connection that was torn down must not apply: it would make the subscription
    /// subscribed with the state of a server-side subscription that is gone, while the resubscribe of the new
    /// connection is skipped.
    /// </summary>
    [Collection("Integration")]
    public class StaleReplyTests : IAsyncLifetime
    {
        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public async Task InitializeAsync()
        {
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
        public async Task SubscribeReplyOfTornDownConnectionIsDiscarded()
        {
            var subscribedEvents = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeSubscribedEventArgs>();
            var pubs = System.Threading.Channels.Channel.CreateUnbounded<CentrifugePublicationEventArgs>();

            // The first subscribe is held unanswered; later ones get channel id 42.
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd => cmd.Subscribe != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            _server.OnSubscribe = (channel, req) => new SubscribeResult { Id = (req.Flag & 1) != 0 ? 42 : 0 };

            _client!.Connect();
            await _client.ReadyAsync();

            var sub = _client.NewSubscription("stale-reply-channel");
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (_, e) => pubs.Writer.TryWrite(e);
            sub.Subscribe();
            var first = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The connection ends with the subscribe in flight; the next one subscribes again.
            NoPing.Fire(_client);
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, _server.Received.Count(c => c.Subscribe != null));

            // The reply to the first subscribe, delivered late: no call waits for it any more.
            await _server.SendReplyAsync(new Reply { Id = first.Id, Subscribe = new SubscribeResult { Id = 77 } });
            await _server.PublishIdAsync(77, Encoding.UTF8.GetBytes("{\"stale\":true}"));
            await _server.PublishIdAsync(42, Encoding.UTF8.GetBytes("{\"ok\":true}"));

            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "the stale reply registered its channel id");
            Assert.False(subscribedEvents.Reader.TryRead(out _), "the stale reply raised Subscribed");
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
        }
    }
}
