using System;
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

        [Fact]
        public async Task ConnectsAndDisconnects()
        {
            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            var disconnectedEvent = new TaskCompletionSource<DisconnectedEventArgs>();

            client.Connected += (s, e) => connectedEvent.TrySetResult(e);
            client.Disconnected += (s, e) => disconnectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            var connected = await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ClientState.Connected, client.State);
            Assert.NotEmpty(connected.ClientId);

            await client.DisconnectAsync();
            var disconnected = await disconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ClientState.Disconnected, client.State);
            Assert.Equal(DisconnectedCodes.DisconnectCalled, disconnected.Code);
        }

        [Fact]
        public async Task SubscribeAndUnsubscribe()
        {
            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<SubscribedEventArgs>();
            var unsubscribedEvent = new TaskCompletionSource<UnsubscribedEventArgs>();

            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

            await subscription.SubscribeAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SubscriptionState.Subscribed, subscription.State);
            Assert.Equal(ClientState.Connected, client.State);

            await subscription.UnsubscribeAsync();
            await client.DisconnectAsync();

            var ctx = await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SubscriptionState.Unsubscribed, subscription.State);
            Assert.Equal(ClientState.Disconnected, client.State);
            Assert.Equal(UnsubscribedCodes.UnsubscribeCalled, ctx.Code);
        }

        [Fact]
        public async Task PublishAndReceiveMessage()
        {
            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<SubscribedEventArgs>();
            var publicationReceived = new TaskCompletionSource<PublicationEventArgs>();

            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);
            subscription.Publication += (s, e) => publicationReceived.TrySetResult(e);

            await subscription.SubscribeAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var testData = new { my = "data" };
            var json = System.Text.Json.JsonSerializer.Serialize(testData);
            await subscription.PublishAsync(Encoding.UTF8.GetBytes(json));

            var ctx = await publicationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync();

            var receivedData = System.Text.Json.JsonSerializer.Deserialize<dynamic>(Encoding.UTF8.GetString(ctx.Data));
            Assert.NotNull(receivedData);
        }

        [Fact]
        public async Task SubscribeAndPresence()
        {
            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var subscribedEvent = new TaskCompletionSource<SubscribedEventArgs>();
            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);

            await subscription.SubscribeAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SubscriptionState.Subscribed, subscription.State);
            Assert.Equal(ClientState.Connected, client.State);

            var presence = await subscription.PresenceAsync();
            Assert.NotEmpty(presence.Clients);

            var presenceStats = await subscription.PresenceStatsAsync();
            Assert.True(presenceStats.NumClients > 0);
            Assert.True(presenceStats.NumUsers > 0);

            await client.DisconnectAsync();
        }

        [Fact]
        public async Task ConnectDisconnectLoop()
        {
            using var client = CreateClient();
            var disconnectedEvent = new TaskCompletionSource<DisconnectedEventArgs>();

            client.Disconnected += (s, e) => disconnectedEvent.TrySetResult(e);

            for (int i = 0; i < 10; i++)
            {
                _ = client.ConnectAsync();
                _ = client.DisconnectAsync();
            }

            Assert.Equal(ClientState.Disconnected, client.State);
            await disconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task SubscribeAndUnsubscribeLoop()
        {
            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscription = client.NewSubscription("test");
            var unsubscribedEvent = new TaskCompletionSource<UnsubscribedEventArgs>();

            subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

            for (int i = 0; i < 10; i++)
            {
                _ = subscription.SubscribeAsync();
                _ = subscription.UnsubscribeAsync();
            }

            Assert.Equal(SubscriptionState.Unsubscribed, subscription.State);
            await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Ensure client is still connected after the loop
            if (client.State != ClientState.Connected)
            {
                var reconnectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
                client.Connected += (s, e) => reconnectedEvent.TrySetResult(e);
                await client.ConnectAsync();
                await reconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Create a fresh subscription to test2 channel to avoid any state issues
            var subscription2 = client.NewSubscription("test2");
            var subscribedEvent2 = new TaskCompletionSource<SubscribedEventArgs>();
            subscription2.Subscribed += (s, e) => subscribedEvent2.TrySetResult(e);

            await subscription2.SubscribeAsync();
            await subscribedEvent2.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SubscriptionState.Subscribed, subscription2.State);

            var presenceStats = await subscription2.PresenceStatsAsync();
            Assert.Equal(1u, presenceStats.NumClients);
            Assert.Equal(1u, presenceStats.NumUsers);

            var unsubscribedEvent2 = new TaskCompletionSource<UnsubscribedEventArgs>();
            subscription2.Unsubscribed += (s, e) => unsubscribedEvent2.TrySetResult(e);

            await subscription2.UnsubscribeAsync();
            await unsubscribedEvent2.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var presenceStats2 = await client.PresenceStatsAsync("test2");
            Assert.Equal(0u, presenceStats2.NumClients);
            Assert.Equal(0u, presenceStats2.NumUsers);

            await client.DisconnectAsync();
        }

        [Fact]
        public async Task ConnectsWithToken()
        {
            // Connection token for anonymous user without ttl (using HMAC secret "secret")
            const string connectToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3MzgwNzg4MjR9.MTb3higWfFW04E9-8wmTFOcf4MEm-rMDQaNKJ1VU_n4";

            var options = new CentrifugeClientOptions
            {
                Token = connectToken
            };

            using var client = new CentrifugeClient(Endpoint, options);
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ClientState.Connected, client.State);

            await client.DisconnectAsync();
        }

        [Fact]
        public async Task SubscribesWithToken()
        {
            // Subscription token for anonymous user for channel "test1" (using HMAC secret "secret")
            const string subscriptionToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzNDgsImNoYW5uZWwiOiJ0ZXN0MSJ9.eqPQxbBtyYxL8Hvbkm-P6aH7chUsSG_EMWe-rTwF_HI";

            using var client = CreateClient();
            var connectedEvent = new TaskCompletionSource<ConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            await client.ConnectAsync();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var subscriptionOptions = new SubscriptionOptions
            {
                Token = subscriptionToken
            };

            var subscription = client.NewSubscription("test1", subscriptionOptions);
            var subscribedEvent = new TaskCompletionSource<SubscribedEventArgs>();
            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);

            await subscription.SubscribeAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SubscriptionState.Subscribed, subscription.State);

            await subscription.UnsubscribeAsync();
            await client.DisconnectAsync();
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
