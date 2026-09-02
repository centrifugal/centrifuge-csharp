using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for "state invalidated" handling: unsubscribe code 2502 (per-subscription)
    /// and disconnect code 3014 (connection-wide). On these the client drops cached
    /// tokens (and recovery position / delta base) so a fresh token is obtained and
    /// the subscription re-syncs. Private fields aren't asserted directly; behavior is
    /// observed over the wire against the in-process <see cref="FakeCentrifugoServer"/>.
    /// </summary>
    [Collection("Integration")]
    public class StateInvalidationTests : IAsyncLifetime
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

        private string? LastConnectToken() =>
            _server.Received.Where(c => c.Connect != null).Select(c => c.Connect.Token).LastOrDefault();

        [Fact]
        public async Task Unsubscribe2502ClearsSubTokenAndResubscribes()
        {
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
            _client.Connect();
            await _client.ReadyAsync();

            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var tokenCalls = 0;
            var sub = _client.NewSubscription("ch", new CentrifugeSubscriptionOptions
            {
                GetToken = _ => Task.FromResult($"t{Interlocked.Increment(ref tokenCalls)}")
            });
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("t1", _server.LastSubscribe()!.Token);

            // Server sends "state invalidated" unsubscribe — the subscription must drop
            // its token and resubscribe with a freshly fetched one.
            await _server.SendPushAsync(new Push
            {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = CentrifugeUnsubscribedCodes.StateInvalidated, Reason = "state invalidated" }
            });

            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("t2", _server.LastSubscribe()!.Token);
        }

        [Fact]
        public async Task Unsubscribe2502RecoverableResubscribesUnrecovered()
        {
            // A recoverable subscription must resubscribe REQUESTING recovery from
            // the sentinel epoch "_" the server can't match → was_recovering=true,
            // recovered=false (so the app reloads via its recovery-failure path).
            _server.OnSubscribe = (channel, req) =>
                new SubscribeResult { Recoverable = true, Epoch = "server-epoch", Offset = 5 };

            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
            _client.Connect();
            await _client.ReadyAsync();

            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = _client.NewSubscription("ch", new CentrifugeSubscriptionOptions());
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(_server.LastSubscribe()!.Recover, "initial subscribe does not request recovery");

            await _server.SendPushAsync(new Push
            {
                Channel = "ch",
                Unsubscribe = new Unsubscribe { Code = CentrifugeUnsubscribedCodes.StateInvalidated, Reason = "state invalidated" }
            });
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var req = _server.LastSubscribe()!;
            Assert.True(req.Recover, "resubscribe requests recovery (recover left true)");
            Assert.Equal("_", req.Epoch);
            Assert.Equal(0UL, req.Offset);
        }

        [Fact]
        public async Task Disconnect3014ResetsServerSubRecoveryPosition()
        {
            // A server-side subscription (pushed via the Connect reply's Subs field,
            // not a client-side CentrifugeSubscription) is recoverable with a cached
            // position from the very first connect. A 3014 disconnect must reset that
            // cached offset/epoch to the sentinel too, or the reconnect keeps
            // requesting recovery from the now-stale pre-invalidation position —
            // defeating the point of state invalidation.
            _server.ConnectResult = new ConnectResult
            {
                Client = "fake-client",
                Version = "0.0.0",
                Ping = 25,
                Subs =
                {
                    ["ch"] = new SubscribeResult
                    {
                        Recoverable = true,
                        Positioned = true,
                        Epoch = "server-epoch",
                        Offset = 42
                    }
                }
            };

            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions
            {
                GetToken = () => Task.FromResult("conn-token"),
                MinReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100)
            });

            var serverSubscribedEvents = NewChannel<CentrifugeServerSubscribedEventArgs>();
            _client.ServerSubscribed += (_, e) => serverSubscribedEvents.Writer.TryWrite(e);

            _client.Connect();
            await _client.ReadyAsync();
            await serverSubscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            List<ConnectRequest> Connects() =>
                _server.Received.Where(c => c.Connect != null).Select(c => c.Connect).ToList();

            // First connect has no cached position yet, so it doesn't request recovery.
            var firstConnects = Connects();
            Assert.Single(firstConnects);
            Assert.False(firstConnects[0].Subs.ContainsKey("ch"), "initial connect does not request recovery");

            // Server sends "state invalidated" disconnect — the client must reset the
            // server-side subscription's cached recovery position before reconnecting.
            await _server.SendPushAsync(new Push
            {
                Disconnect = new Disconnect { Code = CentrifugeDisconnectedCodes.StateInvalidated, Reason = "state invalidated" }
            });

            // Second ServerSubscribed event = reconnected and server subs reprocessed.
            await serverSubscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var connects = Connects();
            Assert.True(connects.Count >= 2, "expected a reconnect Connect command");
            var reconnectRequest = connects[1];
            Assert.True(reconnectRequest.Subs.ContainsKey("ch"), "reconnect requests recovery for server-side sub");
            var subReq = reconnectRequest.Subs["ch"];
            Assert.True(subReq.Recover, "recoverable flag preserved");
            Assert.Equal("_", subReq.Epoch);
            Assert.Equal(0UL, subReq.Offset);
        }

        [Fact]
        public async Task Disconnect3014ClearsConnTokenRefreshesAndInvalidatesSubs()
        {
            var connTokenCalls = 0;
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions
            {
                Token = "conn-token-0",
                GetToken = () => Task.FromResult($"conn-token-{Interlocked.Increment(ref connTokenCalls)}"),
                MinReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100)
            });
            _client.Connect();
            await _client.ReadyAsync();

            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = _client.NewSubscription("ch", new CentrifugeSubscriptionOptions { Token = "sub-token-0" });
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("conn-token-0", LastConnectToken());

            // Server sends "state invalidated" disconnect — the client must clear its
            // connection token, fetch a fresh one via GetToken on reconnect, and
            // invalidate the subscription (resubscribe with no token).
            await _server.SendPushAsync(new Push
            {
                Disconnect = new Disconnect { Code = CentrifugeDisconnectedCodes.StateInvalidated, Reason = "state invalidated" }
            });

            // Second Subscribed event = reconnected + resubscribed.
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connTokenCalls >= 1, "connection token getter must be called after 3014");
            Assert.Equal("conn-token-1", LastConnectToken());
            Assert.Equal("", _server.LastSubscribe()!.Token);
        }
    }
}
