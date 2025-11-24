using System;
using System.Collections.Generic;
using System.Linq;
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

            var receivedData = System.Text.Json.JsonSerializer.Deserialize<dynamic>(Encoding.UTF8.GetString(ctx.Data.Span));
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

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ConnectsAndSubscribesWithGetToken(CentrifugeTransportType transport, string endpoint)
        {
            // Run multiple iterations to ensure consistency with async token retrieval
            for (int iteration = 0; iteration < 5; iteration++)
            {
                // Connection token for anonymous user without ttl (using HMAC secret "secret")
                const string connectToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3MzgwNzg4MjR9.MTb3higWfFW04E9-8wmTFOcf4MEm-rMDQaNKJ1VU_n4";
                int numConnectTokenCalls = 0;
                int numSubscribeTokenCalls = 0;

                var options = new CentrifugeClientOptions
                {
                    GetToken = async () =>
                    {
                        // Sleep for a random time between 0 and 100 milliseconds to emulate network
                        await Task.Delay(Random.Shared.Next(0, 100));
                        numConnectTokenCalls++;
                        return connectToken;
                    }
                };

                using var client = CreateClient(transport, endpoint, options);
                var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
                client.Connected += (s, e) => connectedEvent.TrySetResult(e);

                // Subscription token for anonymous user for channel "test1" (using HMAC secret "secret")
                const string subscriptionToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzNDgsImNoYW5uZWwiOiJ0ZXN0MSJ9.eqPQxbBtyYxL8Hvbkm-P6aH7chUsSG_EMWe-rTwF_HI";

                var subscriptionOptions = new CentrifugeSubscriptionOptions
                {
                    GetToken = async (channel) =>
                    {
                        // Sleep for a random time between 0 and 100 milliseconds to emulate network
                        await Task.Delay(Random.Shared.Next(0, 100));
                        numSubscribeTokenCalls++;
                        return subscriptionToken;
                    }
                };

                client.Connect();

                var subscription = client.NewSubscription("test1", subscriptionOptions);
                var unsubscribedEvent = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();
                subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

                subscription.Subscribe(); await subscription.ReadyAsync();
                await subscription.ReadyAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);
                Assert.Equal(CentrifugeClientState.Connected, client.State);

                subscription.Unsubscribe();
                client.Disconnect();

                await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, subscription.State);
                Assert.Equal(CentrifugeClientState.Disconnected, client.State);

                Assert.Equal(1, numConnectTokenCalls);
                Assert.Equal(1, numSubscribeTokenCalls);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SubscribesAndUnsubscribesFromManySubsWithGetToken(CentrifugeTransportType transport, string endpoint)
        {
            // Run multiple iterations to ensure consistency with async token retrieval
            for (int iteration = 0; iteration < 5; iteration++)
            {
                using var client = CreateClient(transport, endpoint);

                var channels = new[] { "test1", "test2", "test3", "test4", "test5" };

                // Subscription tokens for anonymous users without ttl (using HMAC secret "secret")
                var testTokens = new Dictionary<string, string>
                {
                    ["test1"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzNDgsImNoYW5uZWwiOiJ0ZXN0MSJ9.eqPQxbBtyYxL8Hvbkm-P6aH7chUsSG_EMWe-rTwF_HI",
                    ["test2"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzODcsImNoYW5uZWwiOiJ0ZXN0MiJ9.tTJB3uSa8XpEmCvfkmrSKclijofnJ5RkQk6L2SaGtUE",
                    ["test3"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzOTgsImNoYW5uZWwiOiJ0ZXN0MyJ9.nyLcMrIot441CszOKska7kQIjo2sEm8pSxV1XWfNCsI",
                    ["test4"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzI0MDksImNoYW5uZWwiOiJ0ZXN0NCJ9.wWAX2AhJX6Ep4HVexQWSVF3-cWytVhzY9Pm7QsMdCsI",
                    ["test5"] = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzI0MTgsImNoYW5uZWwiOiJ0ZXN0NSJ9.hCSfpHYws5TXLKkN0bW0DU6C-wgEUNuhGaIy8W1sT9o"
                };

                client.Connect();

                var subscriptions = new List<CentrifugeSubscription>();
                var unsubscribedPromises = new List<Task<CentrifugeUnsubscribedEventArgs>>();

                foreach (var channel in channels)
                {
                    var subscriptionOptions = new CentrifugeSubscriptionOptions
                    {
                        GetToken = async (ch) =>
                        {
                            // Sleep for a random time between 0 and 100 milliseconds to emulate network
                            await Task.Delay(Random.Shared.Next(0, 100));
                            return testTokens[ch];
                        }
                    };

                    var subscription = client.NewSubscription(channel, subscriptionOptions);
                    var unsubscribedEvent = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();
                    subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);
                    unsubscribedPromises.Add(unsubscribedEvent.Task);

                    subscription.Subscribe();
                    subscriptions.Add(subscription);
                }

                // Wait until all subscriptions are in the Subscribed state
                foreach (var subscription in subscriptions)
                {
                    await subscription.ReadyAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);
                }

                // The client itself should be connected now
                Assert.Equal(CentrifugeClientState.Connected, client.State);

                // Unsubscribe from all and then disconnect
                foreach (var subscription in subscriptions)
                {
                    subscription.Unsubscribe();
                }
                client.Disconnect();

                // Wait until all 'unsubscribed' events are received
                await Task.WhenAll(unsubscribedPromises).WaitAsync(TimeSpan.FromSeconds(5));

                // Confirm each subscription is now Unsubscribed
                foreach (var subscription in subscriptions)
                {
                    Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, subscription.State);
                }

                // The client should be disconnected
                Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RetriesConnectionGetTokenError(CentrifugeTransportType transport, string endpoint)
        {
            bool shouldThrowConnectError = true;
            const string connectToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3MzgwNzg4MjR9.MTb3higWfFW04E9-8wmTFOcf4MEm-rMDQaNKJ1VU_n4";
            int numConnectTokenCalls = 0;

            var options = new CentrifugeClientOptions
            {
                GetToken = async () =>
                {
                    numConnectTokenCalls++;
                    if (shouldThrowConnectError)
                    {
                        shouldThrowConnectError = false;
                        throw new Exception("Connection token error");
                    }
                    return connectToken;
                },
                MinReconnectDelay = TimeSpan.FromMilliseconds(1)
            };

            using var client = CreateClient(transport, endpoint, options);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            client.Connect();
            await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(2, numConnectTokenCalls); // Ensure GetToken was retried

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RetriesSubscriptionGetTokenError(CentrifugeTransportType transport, string endpoint)
        {
            const string connectToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3MzgwNzg4MjR9.MTb3higWfFW04E9-8wmTFOcf4MEm-rMDQaNKJ1VU_n4";
            const string subscriptionToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3Mzc1MzIzNDgsImNoYW5uZWwiOiJ0ZXN0MSJ9.eqPQxbBtyYxL8Hvbkm-P6aH7chUsSG_EMWe-rTwF_HI";

            bool shouldThrowSubscriptionError = true;
            int numSubscribeTokenCalls = 0;

            var options = new CentrifugeClientOptions
            {
                GetToken = async () => connectToken
            };

            using var client = CreateClient(transport, endpoint, options);

            var subscriptionOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    numSubscribeTokenCalls++;
                    if (shouldThrowSubscriptionError)
                    {
                        shouldThrowSubscriptionError = false;
                        throw new Exception("Subscription token error");
                    }
                    return subscriptionToken;
                },
                MinResubscribeDelay = TimeSpan.FromMilliseconds(1)
            };

            var subscription = client.NewSubscription("test1", subscriptionOptions);
            var subscribedEvent = new TaskCompletionSource<CentrifugeSubscribedEventArgs>();
            subscription.Subscribed += (s, e) => subscribedEvent.TrySetResult(e);

            client.Connect();
            subscription.Subscribe(); await subscription.ReadyAsync();
            await subscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, subscription.State);
            Assert.Equal(2, numSubscribeTokenCalls); // Ensure GetToken was retried

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task DisconnectedWithUnauthorized(CentrifugeTransportType transport, string endpoint)
        {
            var options = new CentrifugeClientOptions
            {
                GetToken = async () =>
                {
                    await Task.Yield();
                    throw new CentrifugeUnauthorizedException("");
                }
            };

            using var client = CreateClient(transport, endpoint, options);
            var disconnectedEvent = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>();

            client.Disconnected += (s, e) => disconnectedEvent.TrySetResult(e);

            client.Connect();

            var ctx = await disconnectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            Assert.Equal(CentrifugeDisconnectedCodes.Unauthorized, ctx.Code);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task UnsubscribedWithUnauthorized(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            var connectedEvent = new TaskCompletionSource<CentrifugeConnectedEventArgs>();
            client.Connected += (s, e) => connectedEvent.TrySetResult(e);

            var subscriptionOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) => throw new CentrifugeUnauthorizedException("")
            };

            var subscription = client.NewSubscription("test", subscriptionOptions);
            var unsubscribedEvent = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>();
            subscription.Unsubscribed += (s, e) => unsubscribedEvent.TrySetResult(e);

            client.Connect();
            subscription.Subscribe();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Connected, client.State);

            var unsubCtx = await unsubscribedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, subscription.State);
            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, unsubCtx.Code);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task DeltaPublications(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var channelName = $"delta_test_{Guid.NewGuid():N}";
            var subscriptionOptions = new CentrifugeSubscriptionOptions
            {
                Delta = "fossil"
            };

            var subscription = client.NewSubscription(channelName, subscriptionOptions);
            var publications = new List<CentrifugePublicationEventArgs>();
            var allReceived = new TaskCompletionSource<bool>();

            subscription.Publication += (s, e) =>
            {
                publications.Add(e);
                if (publications.Count >= 3)
                {
                    allReceived.TrySetResult(true);
                }
            };

            subscription.Subscribe();
            await subscription.ReadyAsync();

            // Publish 3 messages via Centrifugo server HTTP API
            // Use larger messages to ensure delta compression is beneficial
            using var httpClient = new System.Net.Http.HttpClient();
            var largeText = string.Join(" ", Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit.", 20));
            var messages = new[]
            {
                new { text = largeText, counter = 1, data = new { field1 = "value1", field2 = "value2", field3 = "value3" } },
                new { text = largeText, counter = 2, data = new { field1 = "value1", field2 = "value2", field3 = "value3" } },  // Similar to first - good for delta
                new { text = largeText + " Additional text.", counter = 3, data = new { field1 = "changed", field2 = "value2", field3 = "value3" } }
            };

            foreach (var msg in messages)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(msg);
                var publishRequest = new
                {
                    channel = channelName,
                    data = msg,
                    delta = true
                };
                var requestJson = System.Text.Json.JsonSerializer.Serialize(publishRequest);
                var content = new System.Net.Http.StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("http://localhost:8000/api/publish", content);
                response.EnsureSuccessStatusCode();
            }

            // Wait for all publications to be received
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Verify all 3 messages were received correctly
            Assert.Equal(3, publications.Count);

            for (int i = 0; i < 3; i++)
            {
                var receivedJson = Encoding.UTF8.GetString(publications[i].Data.Span);
                var received = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(receivedJson);

                Assert.Equal(messages[i].text, received.GetProperty("text").GetString());
                Assert.Equal(messages[i].counter, received.GetProperty("counter").GetInt32());
                Assert.Equal(messages[i].data.field1, received.GetProperty("data").GetProperty("field1").GetString());
                Assert.Equal(messages[i].data.field2, received.GetProperty("data").GetProperty("field2").GetString());
                Assert.Equal(messages[i].data.field3, received.GetProperty("data").GetProperty("field3").GetString());
            }

            subscription.Unsubscribe();
            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task HistoryWithDifferentLimits(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var channelName = $"history_test_{Guid.NewGuid():N}";
            var subscription = client.NewSubscription(channelName);

            subscription.Subscribe();
            await subscription.ReadyAsync();

            // Publish 3 messages via Centrifugo server HTTP API
            using var httpClient = new System.Net.Http.HttpClient();
            var messages = new[]
            {
                new { text = "Message 1", id = 1 },
                new { text = "Message 2", id = 2 },
                new { text = "Message 3", id = 3 }
            };

            foreach (var msg in messages)
            {
                var publishRequest = new
                {
                    channel = channelName,
                    data = msg
                };
                var requestJson = System.Text.Json.JsonSerializer.Serialize(publishRequest);
                var content = new System.Net.Http.StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("http://localhost:8000/api/publish", content);
                response.EnsureSuccessStatusCode();
            }

            // Wait a bit to ensure messages are stored in history
            await Task.Delay(100);

            // Test 1: Limit -1 returns full history without limit
            var historyFull = await subscription.HistoryAsync(new CentrifugeHistoryOptions { Limit = -1 });
            Assert.Equal(3, historyFull.Publications.Length);
            Assert.NotEmpty(historyFull.Epoch);
            Assert.True(historyFull.Offset > 0);

            // Verify messages are in correct order (oldest first by default)
            for (int i = 0; i < 3; i++)
            {
                var receivedJson = Encoding.UTF8.GetString(historyFull.Publications[i].Data.Span);
                var received = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(receivedJson);
                Assert.Equal(messages[i].id, received.GetProperty("id").GetInt32());
            }

            // Test 2: Limit 0 returns only stream position, no publications
            var historyEmpty = await subscription.HistoryAsync(new CentrifugeHistoryOptions { Limit = 0 });
            Assert.Empty(historyEmpty.Publications);
            Assert.NotEmpty(historyEmpty.Epoch);
            Assert.True(historyEmpty.Offset > 0);

            // Test 3: Limit 2 returns last 2 publications
            var historyLimited = await subscription.HistoryAsync(new CentrifugeHistoryOptions { Limit = 2 });
            Assert.Equal(2, historyLimited.Publications.Length);
            Assert.NotEmpty(historyLimited.Epoch);
            Assert.True(historyLimited.Offset > 0);

            // Should return the oldest 2 messages
            for (int i = 0; i < 2; i++)
            {
                var receivedJson = Encoding.UTF8.GetString(historyLimited.Publications[i].Data.Span);
                var received = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(receivedJson);
                Assert.Equal(messages[i].id, received.GetProperty("id").GetInt32());
            }

            // Test 4: Reverse order returns newest first
            var historyReverse = await subscription.HistoryAsync(new CentrifugeHistoryOptions { Limit = -1, Reverse = true });
            Assert.Equal(3, historyReverse.Publications.Length);

            // Verify messages are in reverse order (newest first)
            for (int i = 0; i < 3; i++)
            {
                var receivedJson = Encoding.UTF8.GetString(historyReverse.Publications[i].Data.Span);
                var received = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(receivedJson);
                Assert.Equal(messages[2 - i].id, received.GetProperty("id").GetInt32());
            }

            subscription.Unsubscribe();
            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task PresenceReturnsCurrentClient(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var channelName = $"presence_test_{Guid.NewGuid():N}";
            var subscription = client.NewSubscription(channelName);

            subscription.Subscribe();
            await subscription.ReadyAsync();

            // Call presence and verify it returns exactly 1 client (our connection)
            var presence = await subscription.PresenceAsync();

            Assert.NotNull(presence);
            Assert.NotNull(presence.Clients);
            Assert.Single(presence.Clients);

            // Verify the client info
            var clientInfo = presence.Clients.First().Value;
            Assert.NotEmpty(clientInfo.Client);

            // Call presence stats and verify counts
            var presenceStats = await subscription.PresenceStatsAsync();

            Assert.NotNull(presenceStats);
            Assert.Equal(1u, presenceStats.NumClients);
            Assert.Equal(1u, presenceStats.NumUsers);

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
