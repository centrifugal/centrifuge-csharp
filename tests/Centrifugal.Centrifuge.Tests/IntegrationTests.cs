using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        public async Task SubscriptionRetriesAfterMultipleGetTokenErrors(CentrifugeTransportType transport, string endpoint)
        {
            // GetToken throws 3 times, succeeds on 4th call.
            // Bug: ScheduleResubscribeAsync doesn't catch errors from its inner retry, so
            // after the 2nd failure the subscription is permanently stuck in Subscribing state.
            int callCount = 0;
            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    Interlocked.Increment(ref callCount);
                    if (callCount < 4)
                        throw new Exception("token error");
                    return ""; // insecure mode — empty token accepted
                },
                MinResubscribeDelay = TimeSpan.FromMilliseconds(1),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50)
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription("test-retry-multi", subOptions);
            var subscribedTcs = new TaskCompletionSource<bool>();
            sub.Subscribed += (_, _) => subscribedTcs.TrySetResult(true);
            sub.Subscribe();

            // Without fix: stuck in Subscribing after 2nd GetToken failure → times out
            // With fix: retries until success on 4th call
            await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(4, callCount);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task HandleSubscribeTimeout_TriggersCleanReconnectAndResubscribe(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            });
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription("test-sub-timeout");
            sub.Subscribe();
            await sub.ReadyAsync();

            var connectedTcs = new TaskCompletionSource<bool>();
            var subscribedTcs = new TaskCompletionSource<bool>();
            client.Connected += (_, _) => connectedTcs.TrySetResult(true);
            sub.Subscribed += (_, _) => subscribedTcs.TrySetResult(true);

            // Directly invoke the internal method to simulate subscribe timeout path.
            // Bug: this calls StartConnectingAsync (opens transport) then CleanupTransportAsync
            // (destroys it), leaving OnTransportOpened racing with ScheduleReconnectAsync.
            // Fix: directly transitions state without creating a throwaway transport.
            await client.HandleSubscribeTimeoutAsync();

            await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task NoPing_TriggersResubscribeAfterReconnect(CentrifugeTransportType transport, string endpoint)
        {
            using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50)
            });
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription("test-noping-resubscribe");
            sub.Subscribe();
            await sub.ReadyAsync();

            // Listen for the Subscribing event — if the bug is present, NoPing reconnect
            // goes through StartConnectingAsync which skips MoveToSubscribing, so the
            // subscription stays in Subscribed state and never fires Subscribing again.
            var subscribingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Subscribing += (_, _) => subscribingTcs.TrySetResult(true);
            sub.Subscribed += (_, _) => subscribedTcs.TrySetResult(true);

            // Simulate what the ping timer fires when server stops pinging.
            await client.HandleNoPingAsync();

            // Subscription must cycle through Subscribing → Subscribed on the fresh connection.
            // Without the fix this times out because subscriptions are never moved to Subscribing.
            await subscribingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ResubscribeAsync_GetTokenUnauthorized_DoesNotDeadlock(CentrifugeTransportType transport, string endpoint)
        {
            // Bug: ResubscribeAsync holds _stateLock while awaiting StartSubscribingAsync.
            // When GetToken throws CentrifugeUnauthorizedException, SetUnsubscribedAsync
            // tries to acquire the same _stateLock → deadlock.
            // Fix: replace _stateLock hold with a quick _stateChangeLock check only.
            int callCount = 0;
            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n >= 2)
                        throw new CentrifugeUnauthorizedException("unauthorized");
                    return ""; // insecure mode — empty token accepted on first call
                }
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription("test-resubscribe-deadlock", subOptions);
            sub.Subscribe();
            await sub.ReadyAsync();
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

            // Without fix: deadlocks → never returns within 3 seconds
            // With fix: SetUnsubscribedAsync runs, sub becomes Unsubscribed
            await sub.ResubscribeAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task MoveToSubscribing_CancelsResubscribeTimer_PreventsDuplicateSubscribeOnReconnect(CentrifugeTransportType transport, string endpoint)
        {
            // Bug: MoveToSubscribing only cancels _resubscribeCts for Subscribed→Subscribing.
            // If already Subscribing (GetToken failed, resubscribe timer pending), the cancel
            // is skipped and a stale armed retry survives.
            // Fix: cancel _resubscribeCts for any non-Unsubscribed state.
            //
            // Determinism notes:
            // - The subscribe pipeline can be triggered more than once around connect
            //   (subscription's own batch + the connect reply's SendSubscribeCommands sweep,
            //   both fire-and-forget Task.Run). So GetToken throws the transient error
            //   exactly once — the only call that can arm a retry — and PARKS every later
            //   call on a never-completing task: a parked stray pipeline can neither arm a
            //   retry nor unsubscribe, keeping the armed/disarmed observations exact.
            // - The retry delay is long enough that the armed timer can never fire during
            //   the test; the test never waits for it, it asserts the cancel directly.
            int callCount = 0;
            using var parkCts = new CancellationTokenSource();
            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n == 1) throw new Exception("transient token error"); // → ScheduleResubscribeAsync
                    await Task.Delay(Timeout.Infinite, parkCts.Token); // park stray pipelines
                    throw new OperationCanceledException();
                },
                MinResubscribeDelay = TimeSpan.FromSeconds(30),
                MaxResubscribeDelay = TimeSpan.FromSeconds(30)
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription("test-stale-timer", subOptions);
            var unsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribedTcs.TrySetResult(true);

            sub.Subscribe();

            // Eventual check: wait until the GetToken(1) failure has propagated through the
            // subscribe pipeline and ScheduleResubscribeAsync has armed the retry.
            await WaitUntilAsync(() => sub.HasPendingResubscribe, "resubscribe retry to be armed");

            // Directly call MoveToSubscribing (simulates what transport reconnect code does
            // when the subscription is already Subscribing).
            // Without fix: state != Subscribed → returns early, _resubscribeCts NOT cancelled.
            // With fix: cancels _resubscribeCts, disarming the pending retry.
            sub.MoveToSubscribing(CentrifugeConnectingCodes.TransportClosed, "transport closed");

            // The cancel happens synchronously inside MoveToSubscribing, and only the
            // single GetToken(1) failure can ever arm a retry — so this is deterministic;
            // no need to wait out the timer to observe its absence.
            Assert.False(sub.HasPendingResubscribe, "stale resubscribe timer survived MoveToSubscribing");
            Assert.False(unsubscribedTcs.Task.IsCompleted, "subscription unexpectedly unsubscribed");
            Assert.True(callCount >= 1, "GetToken was never called");

            parkCts.Cancel(); // release any parked stray pipeline before teardown
            client.Disconnect();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out waiting for: {description}");
                }
                await Task.Delay(10);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SendSubscribeCommand_DoesNotSendAfterUnsubscribeDuringGetToken(CentrifugeTransportType transport, string endpoint)
        {
            // Bug: SendSubscribeCommandAsync has no state re-check after awaiting GetToken.
            // If Unsubscribe() is called while GetToken awaits, the method continues and
            // sends a Subscribe command to the server despite the sub being Unsubscribed,
            // creating a phantom server-side subscription that delivers publications to
            // the client after the client has locally unsubscribed.
            //
            // Fix: add lock(_stateChangeLock) { if (_state != Subscribing) return; } after
            // the GetToken await.

            var getTokenGate = new SemaphoreSlim(0, 1);
            var getTokenCalledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callCount = 0;

            var channelName = $"test-statefence-{Guid.NewGuid():N}";

            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n >= 2)
                    {
                        getTokenCalledTcs.TrySetResult(true);
                        await getTokenGate.WaitAsync(); // block until released
                    }
                    return ""; // insecure mode — empty token accepted
                }
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription(channelName, subOptions);
            var publicationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, _) => publicationReceived.TrySetResult(true);

            var unsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribedTcs.TrySetResult(true);

            // GetToken(1) returns "" → sub becomes Subscribed on server
            sub.Subscribe();
            await sub.ReadyAsync();
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

            // Trigger resubscribe → SendSubscribeCommandAsync → GetToken(2) blocks on gate
            _ = sub.ResubscribeAsync();
            await getTokenCalledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // While GetToken(2) is blocked, unsubscribe — sets local state to Unsubscribed
            // and schedules an Unsubscribe command to be sent to the server
            sub.Unsubscribe();
            await unsubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Give the async Unsubscribe command enough time to reach the server so that
            // any phantom Subscribe (the bug) arrives AFTER Unsubscribe, making the server
            // route publications to us (proving the bug).
            await Task.Delay(500);

            // Release the gate — without the fix, SendSubscribeCommandAsync skips the state
            // check and sends a Subscribe to the server (phantom subscription)
            getTokenGate.Release();
            await Task.Delay(500); // let any phantom Subscribe be processed by the server

            // Publish via the server HTTP API
            using var httpClient = new System.Net.Http.HttpClient();
            var publishRequest = new { channel = channelName, data = new { text = "hello" } };
            var requestJson = System.Text.Json.JsonSerializer.Serialize(publishRequest);
            var content = new System.Net.Http.StringContent(requestJson, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("http://localhost:8000/api/publish", content);
            response.EnsureSuccessStatusCode();

            await Task.Delay(300);

            // Without fix: phantom Subscribe → server routes publication → Publication event fires
            // With fix: no Subscribe sent after state check → server does not route → no event
            Assert.False(publicationReceived.Task.IsCompleted,
                "received publication despite being Unsubscribed — phantom subscribe was sent");

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task PresenceReturnsCurrentClient(CentrifugeTransportType transport, string endpoint)
        {
            using var client1 = CreateClient(transport, endpoint);
            using var client2 = CreateClient(transport, endpoint);

            client1.Connect();
            await client1.ReadyAsync();

            var channelName = $"presence_test_{Guid.NewGuid():N}";
            var subscription1 = client1.NewSubscription(channelName);

            var joinsReceived = new List<CentrifugeJoinEventArgs>();
            var joinTcs1 = new TaskCompletionSource<bool>();
            var joinTcs2 = new TaskCompletionSource<bool>();

            subscription1.Join += (s, e) =>
            {
                joinsReceived.Add(e);
                if (joinsReceived.Count == 1)
                    joinTcs1.TrySetResult(true);
                else if (joinsReceived.Count == 2)
                    joinTcs2.TrySetResult(true);
            };

            subscription1.Subscribe();
            await subscription1.ReadyAsync();

            // Wait for first join event (client1's own join)
            await joinTcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(joinsReceived);
            Assert.Equal(channelName, joinsReceived[0].Channel);
            Assert.NotEmpty(joinsReceived[0].Info.Client);

            // Connect second client and subscribe to same channel - this should trigger second join event
            client2.Connect();
            await client2.ReadyAsync();

            var subscription2 = client2.NewSubscription(channelName);
            subscription2.Subscribe();
            await subscription2.ReadyAsync();

            // Wait for second join event (client2's join)
            await joinTcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, joinsReceived.Count);
            Assert.Equal(channelName, joinsReceived[1].Channel);
            Assert.NotEmpty(joinsReceived[1].Info.Client);

            // Verify the two client IDs are different
            Assert.NotEqual(joinsReceived[0].Info.Client, joinsReceived[1].Info.Client);

            // Call presence and verify it works (returns at least current client's presence)
            var presence = await subscription1.PresenceAsync();

            Assert.NotNull(presence);
            Assert.NotNull(presence.Clients);
            Assert.True(presence.Clients.Count == 2, "2 clients should be present");

            // Call presence stats and verify counts
            var presenceStats = await subscription1.PresenceStatsAsync();

            Assert.NotNull(presenceStats);
            Assert.True(presenceStats.NumClients == 2, "2 clients should be counted");
            Assert.True(presenceStats.NumUsers >= 1, "1 user should be counted");

            subscription1.Unsubscribe();
            subscription2.Unsubscribe();
            client1.Disconnect();
            client2.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task HandleNoPing_AfterDisconnect_DoesNotSpuriouslyReconnect(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 21: HandleNoPingAsync checks _state == Disconnected outside _stateChangeLock.
            // Race: Disconnect() sets state=Disconnected between the check and SetState(Connecting),
            // causing HandleNoPingAsync to overwrite Disconnected with Connecting and trigger a
            // spurious reconnect attempt.
            // Fix: both check and SetState are inside lock(_stateChangeLock).
            //
            // This test fires both operations concurrently across many iterations to expose the race.
            // With the fix every iteration ends in Disconnected; without it the test is flaky.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync();

                var spuriousConnecting = false;
                var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += (_, _) => disconnectedTcs.TrySetResult(true);
                client.Connecting += (_, _) =>
                {
                    if (disconnectedTcs.Task.IsCompleted) spuriousConnecting = true;
                };

                await Task.WhenAll(
                    client.HandleNoPingAsync(),
                    Task.Run(() => client.Disconnect())
                );

                await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50); // let any spurious ScheduleReconnectAsync start

                Assert.False(spuriousConnecting, $"iteration {i}: spurious Connecting after Disconnect");
                Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task HandleSubscribeTimeout_AfterDisconnect_DoesNotSpuriouslyReconnect(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 22: same unlocked race in HandleSubscribeTimeoutAsync. Fix: same lock pattern.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync();

                var spuriousConnecting = false;
                var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += (_, _) => disconnectedTcs.TrySetResult(true);
                client.Connecting += (_, _) =>
                {
                    if (disconnectedTcs.Task.IsCompleted) spuriousConnecting = true;
                };

                await Task.WhenAll(
                    client.HandleSubscribeTimeoutAsync(),
                    Task.Run(() => client.Disconnect())
                );

                await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50);

                Assert.False(spuriousConnecting, $"iteration {i}: spurious Connecting after Disconnect");
                Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task HandleUnsubscribeError_AfterDisconnect_DoesNotSpuriouslyReconnect(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 23: same unlocked race in HandleUnsubscribeErrorAsync. Fix: same lock pattern.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync();

                var spuriousConnecting = false;
                var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += (_, _) => disconnectedTcs.TrySetResult(true);
                client.Connecting += (_, _) =>
                {
                    if (disconnectedTcs.Task.IsCompleted) spuriousConnecting = true;
                };

                await Task.WhenAll(
                    client.HandleUnsubscribeErrorAsync(),
                    Task.Run(() => client.Disconnect())
                );

                await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50);

                Assert.False(spuriousConnecting, $"iteration {i}: spurious Connecting after Disconnect");
                Assert.Equal(CentrifugeClientState.Disconnected, client.State);
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RefreshConnectionToken_DoesNotRefreshAfterDisconnectDuringGetToken(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 26: RefreshConnectionTokenAsync has no state re-check after awaiting GetToken.
            // If Disconnect() is called while GetToken awaits, the method continues and sends a
            // Refresh command despite the client being Disconnected.
            // Fix: lock(_stateChangeLock) { if (_state != Connected) return; } after the await.
            var getTokenGate = new SemaphoreSlim(0, 1);
            var getTokenCalledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callCount = 0;

            var options = new CentrifugeClientOptions
            {
                GetToken = async () =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n >= 2)
                    {
                        getTokenCalledTcs.TrySetResult(true);
                        await getTokenGate.WaitAsync(); // block until released
                    }
                    return ""; // insecure mode — empty token on first call
                }
            };

            using var client = CreateClient(transport, endpoint, options);
            client.Connect();
            await client.ReadyAsync();
            Assert.Equal(CentrifugeClientState.Connected, client.State);

            var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, _) => disconnectedTcs.TrySetResult(true);

            // Trigger the refresh path directly — GetToken(2) will block on the gate
            _ = client.RefreshConnectionTokenAsync();
            await getTokenCalledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Disconnect while GetToken(2) is blocked — sets local state to Disconnected
            client.Disconnect();
            await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);

            // Release the gate — without fix, RefreshConnectionTokenAsync skips the state
            // check and sends a Refresh command over an already-closed transport
            getTokenGate.Release();
            await Task.Delay(300); // let any phantom Refresh attempt complete

            // Client must remain Disconnected; no reconnect triggered by the phantom Refresh
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RefreshSubscriptionToken_DoesNotRefreshAfterUnsubscribeDuringGetToken(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 27: RefreshTokenAsync (subscription) has no state re-check after GetToken.
            // If Unsubscribe() is called while GetToken awaits, the method continues and sends
            // a SubRefresh command despite the subscription being Unsubscribed.
            // Fix: lock(_stateChangeLock) { if (_state != Subscribed) return; } after the await.
            var getTokenGate = new SemaphoreSlim(0, 1);
            var getTokenCalledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callCount = 0;

            var channelName = $"test-subrefresh-fence-{Guid.NewGuid():N}";
            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n >= 2)
                    {
                        getTokenCalledTcs.TrySetResult(true);
                        await getTokenGate.WaitAsync();
                    }
                    return ""; // insecure mode
                }
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription(channelName, subOptions);
            sub.Subscribe();
            await sub.ReadyAsync();
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

            var unsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribedTcs.TrySetResult(true);

            // Trigger the token refresh path directly — GetToken(2) blocks
            _ = sub.RefreshTokenAsync();
            await getTokenCalledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Unsubscribe while GetToken(2) is blocked
            sub.Unsubscribe();
            await unsubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);

            // Release the gate — without fix, RefreshTokenAsync skips the state check and
            // sends a SubRefresh command over the server-side unsubscribed channel
            getTokenGate.Release();
            await Task.Delay(300);

            // Subscription must remain Unsubscribed
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ResubscribeAsync_AfterUnsubscribe_DoesNotOverwriteUnsubscribedState(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 28: StartSubscribingAsync called SetState(Subscribing) without _stateChangeLock.
            // Race: ResubscribeAsync (server temp-unsub path) releases the lock after checking state,
            // then Unsubscribe() runs and sets state to Unsubscribed, then StartSubscribingAsync
            // overwrites Unsubscribed with Subscribing — subscription stuck in wrong state.
            // Fix: SetState(Subscribing) is now inside lock(_stateChangeLock) with re-check.
            //
            // Run 50 concurrent iterations. With the fix every iteration ends Unsubscribed.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync();

                var sub = client.NewSubscription($"test-resubscribe-race-{i}");
                sub.Subscribe();
                await sub.ReadyAsync();

                var unsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sub.Unsubscribed += (_, _) => unsubscribedTcs.TrySetResult(true);

                // Fire both concurrently: server-side resubscribe path vs. user unsubscribe.
                // Without fix: StartSubscribingAsync may overwrite the Unsubscribed state set by
                // SetUnsubscribedAsync, leaving the subscription stuck in Subscribing.
                await Task.WhenAll(
                    sub.ResubscribeAsync(),
                    Task.Run(() => sub.Unsubscribe())
                );

                await unsubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50); // let any spurious subscribe attempt start

                Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
                client.Disconnect();
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task ResubscribeAsync_DoesNotSendDuplicateSubscribe_WhenInflightConcurrent(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 29: StartSubscribingAsync called SendSubscribeCommandAsync directly, bypassing
            // the _inflight guard. A concurrent SendSubscribeCommandsAsync from a reconnect batch
            // could also be calling SendSubscribeCommandAsync, resulting in two simultaneous
            // Subscribe commands for the same channel.
            // Fix: StartSubscribingAsync now delegates to SendSubscribeIfNeededAsync which holds
            // _inflight, preventing the duplicate.
            //
            // Test: call ResubscribeAsync while GetToken is blocked, then Unsubscribe while blocked.
            // With the _inflight + state-fence combo, only one subscribe reaches the server and
            // no phantom subscribe is sent after Unsubscribe.
            var getTokenGate = new SemaphoreSlim(0, 1);
            var getTokenCalledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int callCount = 0;
            var channelName = $"test-resubscribe-inflight-{Guid.NewGuid():N}";

            var subOptions = new CentrifugeSubscriptionOptions
            {
                GetToken = async (channel) =>
                {
                    int n = Interlocked.Increment(ref callCount);
                    if (n >= 2)
                    {
                        getTokenCalledTcs.TrySetResult(true);
                        await getTokenGate.WaitAsync();
                    }
                    return ""; // insecure mode
                }
            };

            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync();

            var sub = client.NewSubscription(channelName, subOptions);
            sub.Subscribe();
            await sub.ReadyAsync();

            var publicationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, _) => publicationReceived.TrySetResult(true);

            var unsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribedTcs.TrySetResult(true);

            // Trigger resubscribe path → GetToken(2) blocks
            _ = sub.ResubscribeAsync();
            await getTokenCalledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Unsubscribe while GetToken(2) is blocked
            sub.Unsubscribe();
            await unsubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Give server time to process any pending Unsubscribe command
            await Task.Delay(500);

            // Release gate — without fix, a duplicate Subscribe could race through
            getTokenGate.Release();
            await Task.Delay(500);

            // Publish to channel — no Publication event should fire (not subscribed)
            using var httpClient = new System.Net.Http.HttpClient();
            var publishRequest = new { channel = channelName, data = new { text = "hello" } };
            var requestJson = System.Text.Json.JsonSerializer.Serialize(publishRequest);
            var content = new System.Net.Http.StringContent(requestJson, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("http://localhost:8000/api/publish", content);
            response.EnsureSuccessStatusCode();

            await Task.Delay(300);

            Assert.False(publicationReceived.Task.IsCompleted,
                "received publication after Unsubscribe — duplicate Subscribe was sent");
            Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);

            client.Disconnect();
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task Connect_ConcurrentCalls_DoNotCreateDuplicateTransport(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 30: StartConnecting() called SetState(Connecting) without _stateChangeLock.
            // Race: two concurrent Connect() calls both pass the unlocked state checks and both
            // call StartConnecting(), each creating a separate transport. This causes duplicate
            // Connected events, competing transports, and undefined client state.
            // Fix: StartConnecting() re-checks state inside lock before SetState — only the first
            // caller proceeds; subsequent callers see Connecting and return early.
            //
            // Run 50 iterations. With the fix exactly one Connected event fires per pair of
            // concurrent Connect() calls.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });

                int connectedCount = 0;
                var firstConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Connected += (_, _) =>
                {
                    Interlocked.Increment(ref connectedCount);
                    firstConnectedTcs.TrySetResult(true);
                };

                // Fire two Connect() calls simultaneously from different threads.
                // Without fix: both pass the unlocked state check (Disconnected) and both call
                // StartConnecting() → SetState(Connecting) → CreateTransportAsync → Connected.
                // With fix: StartConnecting() re-checks under lock; only one succeeds.
                await Task.WhenAll(
                    Task.Run(() => client.Connect()),
                    Task.Run(() => client.Connect())
                );

                await firstConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(200); // let any duplicate connection attempt complete

                Assert.Equal(1, connectedCount);
                Assert.Equal(CentrifugeClientState.Connected, client.State);

                client.Disconnect();
                await client.ReadyAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }); // drain
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task Subscribe_ConcurrentCalls_DoNotFireDuplicateSubscribingEvent(CentrifugeTransportType transport, string endpoint)
        {
            // Bug 31: Subscribe() checked state without _stateChangeLock, and StartSubscribing()
            // called SetState(Subscribing) without _stateChangeLock. Race: two concurrent Subscribe()
            // calls both pass the unlocked state checks (Unsubscribed) and both call StartSubscribing(),
            // each firing a Subscribing event and potentially sending duplicate subscribe commands.
            // Fix: Subscribe() checks state inside lock; StartSubscribing() re-checks under lock
            // before SetState — only the first caller proceeds.
            //
            // Run 50 iterations. With the fix exactly one Subscribing event fires per pair of
            // concurrent Subscribe() calls.
            for (int i = 0; i < 50; i++)
            {
                using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync(TimeSpan.FromSeconds(5));

                var sub = client.NewSubscription("test-subscribe-concurrent");

                int subscribingCount = 0;
                int subscribedCount = 0;
                var firstSubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sub.Subscribing += (_, _) => Interlocked.Increment(ref subscribingCount);
                sub.Subscribed += (_, _) =>
                {
                    Interlocked.Increment(ref subscribedCount);
                    firstSubscribedTcs.TrySetResult(true);
                };

                // Fire two Subscribe() calls simultaneously from different threads.
                // Without fix: both pass the unlocked state check (Unsubscribed) and both call
                // StartSubscribing() → SetState(Subscribing) → fire Subscribing event.
                // With fix: StartSubscribing() re-checks under lock; only one succeeds.
                await Task.WhenAll(
                    Task.Run(() => sub.Subscribe()),
                    Task.Run(() => sub.Subscribe())
                );

                await firstSubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(200); // let any duplicate subscription attempt complete

                Assert.Equal(1, subscribingCount);
                Assert.Equal(1, subscribedCount);
                Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);

                sub.Unsubscribe();
                client.Disconnect();
                await client.ReadyAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }); // drain
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task SendCommand_CallerCancellation_ThrowsOperationCanceled(CentrifugeTransportType transport, string endpoint)
        {
            // Verifies that when the caller's CancellationToken is cancelled,
            // SendCommandAsync surfaces OperationCanceledException rather than CentrifugeTimeoutException.
            // Pre-cancelled token guarantees the linked Task.Delay completes before any server reply
            // arrives, so we exercise the cancellation-vs-timeout branch deterministically.
            using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
            });
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var rpcTask = client.RpcAsync("ping", ReadOnlyMemory<byte>.Empty, cts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rpcTask);
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RpcAsync_QueuedDuringConnecting_FlushesAfterConnected(CentrifugeTransportType transport, string endpoint)
        {
            // The client admits commands while in Connecting state and adds them to the
            // batch. Before the fix, the batch was only drained by the 1ms timer or by a
            // subsequent enqueue. If the user issued an RpcAsync from the Connecting event
            // handler and there were no subscriptions, the command sat in the batch up to
            // the per-command timeout. The fix kicks a flush from HandleConnectReply.
            using var client = CreateClient(transport, endpoint, new CentrifugeClientOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
            });

            // Issue RPC during Connecting; expect it to complete quickly after connect.
            Task<CentrifugeRpcResult>? rpcTask = null;
            client.Connecting += (_, _) =>
            {
                // Echo handler doesn't need to exist server-side; we only care that the
                // command flushes and either succeeds or fails fast (not hangs on timeout).
                rpcTask = client.RpcAsync("ping", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            };

            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(rpcTask);
            // Must complete (success or error) well under the 10s timeout — the kick
            // from HandleConnectReply drives the flush even with no subscriptions.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await rpcTask!.WaitAsync(watchdog.Token);
            }
            catch (CentrifugeException)
            {
                // Server-side error (method not registered) is fine — we just need the flush to happen.
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task RemoveSubscription_DisposesAndFreesChannelSlot(CentrifugeTransportType transport, string endpoint)
        {
            // After RemoveSubscription, the channel slot must be free and a new subscription
            // must be createable for the same channel. Also verifies that the removed
            // subscription does not leak: re-removing or operating on it is a no-op.
            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));

            var channel = "test-remove-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            for (int i = 0; i < 10; i++)
            {
                var sub = client.NewSubscription(channel);
                var subscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sub.Subscribed += (_, _) => subscribedTcs.TrySetResult(true);
                sub.Subscribe();
                await subscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                client.RemoveSubscription(sub);
                Assert.False(client.Subscriptions.ContainsKey(channel));

                // Calling Dispose again should be safe (idempotent).
                sub.Dispose();
            }
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task PublicApi_AfterDispose_ThrowsObjectDisposedException(CentrifugeTransportType transport, string endpoint)
        {
            // After Dispose, public entry points must throw ObjectDisposedException rather than
            // silently spinning up a new transport / subscription that nothing will clean up.
            var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));
            client.Dispose();

            Assert.Throws<ObjectDisposedException>(() => client.Connect());
            Assert.Throws<ObjectDisposedException>(() => client.NewSubscription("any"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ReadyAsync(TimeSpan.FromSeconds(1)));
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task Subscribe_PermanentError_AllowsResubscribeFromUnsubscribedHandler(CentrifugeTransportType transport, string endpoint)
        {
            // Bug: a permanent subscribe error path entered SetUnsubscribedAsync with _inflight
            // still set. If the user called Subscribe() from the Unsubscribed event handler,
            // the new attempt observed _inflight=true and bailed; the outer finally then cleared
            // _inflight, but nothing rescheduled the resub. The subscription stayed stuck in
            // Subscribing.
            //
            // Fix: _inflight is now cleared BEFORE await SetUnsubscribedAsync in the permanent-error
            // branch, mirroring the timeout/unauthorized/temporary patterns.
            //
            // We simulate a permanent error by subscribing to a server-side namespaced channel
            // with no token — Centrifugo returns code 103 (unauthorized, non-temporary). The user's
            // Unsubscribed handler then calls Subscribe() once more (also failing, but observable).
            using var client = CreateClient(transport, endpoint);
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(5));

            // "$" prefix in default Centrifugo config requires server-side authorization → token-less
            // subscribe yields a non-temporary error (permanent).
            var sub = client.NewSubscription("$forbidden");

            int subscribingCount = 0;
            var secondUnsubscribedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int unsubscribedCount = 0;
            sub.Subscribing += (_, _) => Interlocked.Increment(ref subscribingCount);
            sub.Unsubscribed += (_, _) =>
            {
                if (Interlocked.Increment(ref unsubscribedCount) == 1)
                {
                    // Reentrant Subscribe from the handler. Before the fix this stuck in Subscribing
                    // because _inflight was still held by the outer permanent-error path.
                    sub.Subscribe();
                }
                else
                {
                    secondUnsubscribedTcs.TrySetResult(true);
                }
            };

            sub.Subscribe();

            // Second Unsubscribed must arrive — meaning the second Subscribe actually attempted
            // (transitioned to Subscribing, hit the server again, came back as Unsubscribed).
            await secondUnsubscribedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(subscribingCount >= 2, $"expected ≥2 Subscribing events, got {subscribingCount}");
        }

        [Theory]
        [MemberData(nameof(GetCentrifugeTransportEndpoints))]
        public async Task NextCommandId_SkipsZero_OnWraparound(CentrifugeTransportType transport, string endpoint)
        {
            // Bug: NextCommandId wraps through 0 on overflow; id=0 is reserved for push messages
            // (no reply), so a wrap could collide with pushes. Fix: skip 0 in NextCommandId.
            //
            // We exercise the path directly via reflection to set _commandId near the wrap point.
            using var client = CreateClient(transport, endpoint);
            var clientField = typeof(CentrifugeClient).GetField("_commandId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(clientField);
            // Set just before wrap so the next Interlocked.Increment yields 0 (after cast).
            clientField!.SetValue(client, -1);

            var nextMethod = typeof(CentrifugeClient).GetMethod("NextCommandId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(nextMethod);
            var id = (uint)nextMethod!.Invoke(client, null)!;
            Assert.NotEqual(0u, id);
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
