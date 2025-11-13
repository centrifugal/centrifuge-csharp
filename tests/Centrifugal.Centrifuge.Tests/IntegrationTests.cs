using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Integration tests that require a running Centrifugo server.
    /// Run: docker-compose up -d
    /// </summary>
    [Collection("Integration")]
    public class WebSocketIntegrationTests : IntegrationTestBase
    {
        public WebSocketIntegrationTests() : base("ws://localhost:8000/connection/websocket") { }

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

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ConnectsAndDisconnects(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            var disconnectedEvent = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>();

            client.Connected += (s, e) => connectedEvent.TrySetResult(e);
            client.Disconnected += (s, e) => disconnectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            var connected = await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.NotEmpty(connected.ClientId);

            client.Disconnect();
            var disconnected = await disconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Equal(CentrifugeDisconnectedCodes.DisconnectCalled, disconnected.Code);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SubscribeAndUnsubscribe(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            var unsubscribedEvent = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();

            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

            subscription.Subscribe(); await subscription.ReadyAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);
            Assert.Equal(CentrifugeClientState.Connected, client.State);

            subscription.Unsubscribe();
            client.Disconnect();

            var ctx = await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, subscription.State);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Equal(CentrifugeUnsubscribedCodes.UnsubscribeCalled, ctx.Code);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task PublishAndReceiveMessage(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            var publicationReceived = new TaskCompletionSource<CentrifugePublicationEventArgs>();

            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            subscription.Publication += (s, e) => publicationReceived.TrySetResult(e);

            subscription.Subscribe(); await subscription.ReadyAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var testData = new { my = "data" };
            var json = System.Text.Json.JsonSerializer.Serialize(testData);
            await subscription.PublishAsync(Encoding.UTF8.GetBytes(json));

            var ctx = await publicationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Disconnect();

            var receivedData = System.Text.Json.JsonSerializer.Deserialize<dynamic>(Encoding.UTF8.GetString(ctx.Data));
            Assert.NotNull(receivedData);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SubscribeAndPresence(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);

            subscription.Subscribe(); await subscription.ReadyAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);
            Assert.Equal(CentrifugeClientState.Connected, client.State);

            var presence = await subscription.PresenceAsync();
            Assert.NotEmpty(presence.Clients);

            var presenceStats = await subscription.PresenceStatsAsync();
            Assert.True(presenceStats.NumClients > 0);
            Assert.True(presenceStats.NumUsers > 0);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ConnectDisconnectLoop(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var disconnectedEvent = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>();

            client.Disconnected += (s, e) => disconnectedEvent.TrySetResult(e);

            for (int i = 0; i < 10; i++)
            {
                client.Connect();
                client.Disconnect();
            }

            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            await disconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SubscribeAndUnsubscribeLoop(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var unsubscribedEvent = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();

            subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

            for (int i = 0; i < 10; i++)
            {
                subscription.Subscribe();
                subscription.Unsubscribe();
            }

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, subscription.State);
            await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Ensure client is still connected after the loop
            if (client.State != CentrifugeClientState.Connected)
            {
                var reconnectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
                client.Connected += (s, e) => reconnectedEvent.TrySetResult(e);
                client.Connect(); await client.ReadyAsync();
                await reconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Create a fresh subscription to test2 channel to avoid any state issues
            var subscription2 = client.NewSubscription("test2");
            var subscribedEvent2 = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            subscription2.Subscribed += (s, e) => subscribedEvent2.TrySetResult(e);

            subscription2.Subscribe(); await subscription2.ReadyAsync();
            await subscribedEvent2.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription2.State);

            var presenceStats = await subscription2.PresenceStatsAsync();
            Assert.Equal(1u, presenceStats.NumClients);
            Assert.Equal(1u, presenceStats.NumUsers);

            var unsubscribedEvent2 = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();
            subscription2.Unsubscribed += (s, e) => unsubscribedEvent2.TrySetResult(e);

            subscription2.Unsubscribe();
            await unsubscribedEvent2.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var presenceStats2 = await client.PresenceStatsAsync("test2");
            Assert.Equal(0u, presenceStats2.NumClients);
            Assert.Equal(0u, presenceStats2.NumUsers);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ConnectsWithToken(CentrifugeTransportType transport, string endpoint)
        {
            // Connection token for anonymous user without ttl (using HMAC secret "secret")
            const string connectToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3MzgwNzg4MjR9.MTb3higWfFW04E9-8wmTFOcf4MEm-rMDQaNKJ1VU_n4";

            var options = new CentrifugeClientOptions
            {
                Token = connectToken
            };

            using var client = CreateClient(transport, endpoint, options);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Connected, client.State);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SubscribesWithToken(CentrifugeTransportType transport, string endpoint)
        {
            // Subscription token for anonymous user for channel "test1" (using HMAC secret "secret")
            const string subscriptionToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzNDgsImNoYW5uZWwiOiJ0ZXN0MSJ9.eqPQxbBtyYxL8Hvbkm-P6aH7chUsSG_EMWe-rTwF_HI";

            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect(); await client.ReadyAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscriptionOptions = new CentrifugeSubscriptionOptions
            {
                Token = subscriptionToken
            };

            var subscription = client.NewSubscription("test1", subscriptionOptions);
            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);

            subscription.Subscribe(); await subscription.ReadyAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);

            subscription.Unsubscribe();
            client.Disconnect();
        }
    }

    /// <summary>
    /// Base class for integration tests.
    /// </summary>
    public abstract class IntegrationTestBase
    {
        protected string Endpoint { get; }

        protected IntegrationTestBase(string endpoint)
        {
            Endpoint = endpoint;
        }

        protected CentrifugeClient CreateClient(CentrifugeClientOptions? options = null)
        {
            return new CentrifugeClient(Endpoint, options);
        }

        protected CentrifugeClient CreateClient(CentrifugeTransportType transport, string endpoint, CentrifugeClientOptions? options = null)
        {
            var transportEndpoint = new CentrifugeTransportEndpoint(transport, endpoint);
            return new CentrifugeClient(new[] { transportEndpoint }, options);
        }
    }

    /// <summary>
    /// Extension methods for testing.
    /// </summary>
    public static class TaskExtensions
    {
        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            var completedTask = await Task.WhenAny(task, Task.Delay(System.Threading.Timeout.Infinite, cts.Token));
            if (completedTask != task)
            {
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
            }
            return await task;
        }
    }
}
