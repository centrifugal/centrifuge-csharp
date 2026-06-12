using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Integration tests for CentrifugeSubscriptionOptions.GetState. These mirror the
    /// getState tests in other Centrifugal SDKs and require the docker-compose Centrifugo
    /// (>= 6.8.0) running on localhost:8000.
    /// </summary>
    [Collection("Integration")]
    public class GetStateIntegrationTests : IntegrationTestBase
    {
        public GetStateIntegrationTests() : base("ws://localhost:8000/connection/websocket") { }

        public static IEnumerable<object[]> GetCentrifugeTransportEndpoints()
        {
            yield return new object[]
            {
                CentrifugeTransportType.WebSocket,
                "ws://localhost:8000/connection/websocket"
            };
            yield return new object[]
            {
                CentrifugeTransportType.HttpStream,
                "http://localhost:8000/connection/http_stream"
            };
        }

        private static string UniqueChannel(string ns)
        {
            return ns + ":get_state_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static byte[] Payload(int i)
        {
            return Encoding.UTF8.GetBytes("{\"i\":" + i + "}");
        }

        private async Task<CentrifugeClient> ConnectedClientAsync(CentrifugeTransportType transport, string endpoint)
        {
            var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();
            return client;
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task GetStateCalledOnInitialSubscribeAndRecovers(CentrifugeTransportType transport, string endpoint)
        {
            var channel = UniqueChannel("recovery");

            // Publish 3 messages BEFORE subscribing.
            using var publisher = await ConnectedClientAsync(transport, endpoint);
            for (var i = 1; i <= 3; i++)
            {
                await publisher.PublishAsync(channel, Payload(i));
            }

            using var client = await ConnectedClientAsync(transport, endpoint);

            var getStateCalls = 0;
            // The GetState callback returns zero position — recovery delivers all 3 publications.
            var sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions
            {
                GetState = _ =>
                {
                    Interlocked.Increment(ref getStateCalls);
                    return Task.FromResult(new CentrifugeStreamPosition(0, ""));
                }
            });

            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            var pubs = Channel.CreateUnbounded<CentrifugePublicationEventArgs>();
            sub.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            sub.Publication += (s, e) => pubs.Writer.TryWrite(e);

            sub.Subscribe();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (var i = 1; i <= 3; i++)
            {
                var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("{\"i\":" + i + "}", Encoding.UTF8.GetString(pub.Data.Span));
            }
            Assert.Equal(1, Volatile.Read(ref getStateCalls));
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task GetStateNotCalledWhenRecoverySucceeds(CentrifugeTransportType transport, string endpoint)
        {
            var channel = UniqueChannel("recovery");

            using var publisher = await ConnectedClientAsync(transport, endpoint);
            using var client = await ConnectedClientAsync(transport, endpoint);

            var getStateCalls = 0;
            var sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions
            {
                GetState = _ =>
                {
                    Interlocked.Increment(ref getStateCalls);
                    return Task.FromResult(new CentrifugeStreamPosition(0, ""));
                }
            });

            var subscribedEvents = Channel.CreateUnbounded<CentrifugeSubscribedEventArgs>();
            var pubs = Channel.CreateUnbounded<CentrifugePublicationEventArgs>();
            sub.Subscribed += (s, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (s, e) => pubs.Writer.TryWrite(e);

            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref getStateCalls));

            // Disconnect, publish while away, reconnect — the subscription stays
            // registered (moves to subscribing) and resubscribes automatically with
            // the saved position; recovery succeeds, so GetState must NOT be called
            // again.
            client.Disconnect();

            await publisher.PublishAsync(channel, Payload(1));
            await publisher.PublishAsync(channel, Payload(2));

            client.Connect();

            var resubscribed = await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(resubscribed.Recovered, "expected successful recovery on reconnect");

            for (var i = 1; i <= 2; i++)
            {
                var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("{\"i\":" + i + "}", Encoding.UTF8.GetString(pub.Data.Span));
            }
            Assert.Equal(1, Volatile.Read(ref getStateCalls));
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task GetStateErrorRetried(CentrifugeTransportType transport, string endpoint)
        {
            var channel = UniqueChannel("recovery");

            using var client = await ConnectedClientAsync(transport, endpoint);

            var getStateCalls = 0;
            // First GetState call fails, second succeeds.
            var sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(50),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
                GetState = _ =>
                {
                    if (Interlocked.Increment(ref getStateCalls) == 1)
                    {
                        throw new Exception("simulated DB failure");
                    }
                    return Task.FromResult(new CentrifugeStreamPosition(0, ""));
                }
            });

            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            var errors = Channel.CreateUnbounded<CentrifugeErrorEventArgs>();
            sub.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            sub.Error += (s, e) => errors.Writer.TryWrite(e);

            sub.Subscribe();

            // First GetState fails → error raised → resubscribe scheduled with
            // backoff. Second GetState succeeds → subscribe completes.
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Volatile.Read(ref getStateCalls) >= 2);

            var error = await errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("getState", error.Type);
            var getStateException = Assert.IsType<CentrifugeGetStateException>(error.Exception);
            Assert.Contains("simulated DB failure", getStateException.Message);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task GetStatePersistentFailureKeepsRetrying(CentrifugeTransportType transport, string endpoint)
        {
            var channel = UniqueChannel("recovery");

            using var client = await ConnectedClientAsync(transport, endpoint);

            var getStateCalls = 0;
            var sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(50),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
                GetState = _ =>
                {
                    Interlocked.Increment(ref getStateCalls);
                    throw new Exception("always fails");
                }
            });

            sub.Subscribe();

            // Wait for several retry cycles.
            await Task.Delay(700);

            // Should have retried multiple times while staying in subscribing state.
            Assert.True(Volatile.Read(ref getStateCalls) > 2,
                $"expected more than 2 GetState calls, got {getStateCalls}");
            Assert.Equal(CentrifugeSubscriptionState.Subscribing, sub.State);

            sub.Unsubscribe();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task GetStateCalledAgainOnUnrecoverablePosition(CentrifugeTransportType transport, string endpoint)
        {
            // Uses "smallhistory" namespace with history_size=2. After publishing
            // enough to evict old entries, reconnecting from an old position triggers
            // error 112 (unrecoverable position) because the subscribe request carries
            // the reject_unrecovered flag. The SDK must then call GetState again to
            // reload app state instead of delivering recovered=false on an active
            // subscription.
            var channel = UniqueChannel("smallhistory");

            // Publisher client: used both to publish and to read the current stream
            // top position via history (as the app's backend would).
            using var publisher = await ConnectedClientAsync(transport, endpoint);
            using var client = await ConnectedClientAsync(transport, endpoint);

            var getStateCalls = 0;
            var sub = client.NewSubscription(channel, new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(50),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
                GetState = async ch =>
                {
                    Interlocked.Increment(ref getStateCalls);
                    var history = await publisher.HistoryAsync(ch).ConfigureAwait(false);
                    return new CentrifugeStreamPosition(history.Offset, history.Epoch);
                }
            });

            var subscribedEvents = Channel.CreateUnbounded<CentrifugeSubscribedEventArgs>();
            var pubs = Channel.CreateUnbounded<CentrifugePublicationEventArgs>();
            sub.Subscribed += (s, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (s, e) => pubs.Writer.TryWrite(e);

            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref getStateCalls));

            // Disconnect, then publish enough messages to push the stream beyond
            // recovery (history_size=2, so 5 messages evict old entries).
            client.Disconnect();
            for (var i = 1; i <= 5; i++)
            {
                await publisher.PublishAsync(channel, Payload(i));
            }

            // Reconnect — the subscription resubscribes automatically and tries to
            // recover from the old position, server returns error 112, SDK resets
            // position and calls GetState again.
            client.Connect();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref getStateCalls));

            // Verify live delivery works after the re-sync.
            await publisher.PublishAsync(channel, Encoding.UTF8.GetBytes("{\"live\":true}"));
            var live = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"live\":true}", Encoding.UTF8.GetString(live.Data.Span));
        }
    }
}
