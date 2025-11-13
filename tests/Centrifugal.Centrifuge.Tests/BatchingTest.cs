using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    [Collection("Integration")]
    public class BatchingIntegrationTests
    {
        private const string ServerUrl = "ws://localhost:8000/connection/websocket";

        [Fact]
        public async Task MultipleSubscribes_BeforeConnect_AllSubscribeSuccessfully()
        {
            // This test verifies that subscribing to multiple channels before connect
            // results in all subscriptions being established successfully when connection completes

            var client = new CentrifugeClient(ServerUrl);
            var connectedEvent = new TaskCompletionSource<bool>();

            client.Connected += (s, e) => connectedEvent.TrySetResult(true);

            try
            {
                // Subscribe to 10 channels before connecting
                var subscriptions = new CentrifugeSubscription[10];
                var subscribedTasks = new Task<bool>[10];

                for (int i = 0; i < 10; i++)
                {
                    var sub = client.NewSubscription($"test_batch_{i}");
                    var tcs = new TaskCompletionSource<bool>();
                    subscribedTasks[i] = tcs.Task;

                    sub.Subscribed += (s, e) => tcs.TrySetResult(true);
                    sub.Error += (s, e) => tcs.TrySetException(new Exception($"Subscription error: {e.Message}"));

                    sub.Subscribe();  // Non-blocking, queues for later
                    subscriptions[i] = sub;
                }

                // Verify all are in Subscribing state before connect
                foreach (var sub in subscriptions)
                {
                    Assert.Equal(CentrifugeSubscriptionState.Subscribing, sub.State);
                }

                // Connect - this should batch all subscribe requests
                client.Connect();
                await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Wait for all subscriptions to complete (with timeout)
                await Task.WhenAll(subscribedTasks).WaitAsync(TimeSpan.FromSeconds(5));

                // Verify all subscriptions are now subscribed
                foreach (var sub in subscriptions)
                {
                    Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
                }

                // Cleanup
                foreach (var sub in subscriptions)
                {
                    sub.Unsubscribe();
                }
                client.Disconnect();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        [Fact]
        public async Task MultipleSubscribes_AfterConnect_AllSubscribeSuccessfully()
        {
            // This test verifies that subscribing to multiple channels after connect
            // results in all subscriptions being established successfully (auto-batched with 1ms delay)

            var client = new CentrifugeClient(ServerUrl);
            var connectedEvent = new TaskCompletionSource<bool>();

            client.Connected += (s, e) => connectedEvent.TrySetResult(true);

            try
            {
                // Connect first
                client.Connect();
                await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Subscribe to 10 channels quickly (should be auto-batched)
                var subscriptions = new CentrifugeSubscription[10];
                var subscribedTasks = new Task<bool>[10];

                for (int i = 0; i < 10; i++)
                {
                    var sub = client.NewSubscription($"test_batch_after_{i}");
                    var tcs = new TaskCompletionSource<bool>();
                    subscribedTasks[i] = tcs.Task;

                    sub.Subscribed += (s, e) => tcs.TrySetResult(true);
                    sub.Error += (s, e) => tcs.TrySetException(new Exception($"Subscription error: {e.Message}"));

                    sub.Subscribe();  // Auto-batched with 1ms delay
                    subscriptions[i] = sub;
                }

                // Wait for all subscriptions to complete
                await Task.WhenAll(subscribedTasks).WaitAsync(TimeSpan.FromSeconds(5));

                // Verify all subscriptions are subscribed
                foreach (var sub in subscriptions)
                {
                    Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
                }

                // Cleanup
                foreach (var sub in subscriptions)
                {
                    sub.Unsubscribe();
                }
                client.Disconnect();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        [Fact]
        public async Task UnsubscribeDuringBatchWindow_SkipsUnsubscribed()
        {
            // This test verifies that if a subscription is unsubscribed during the batch window,
            // it is skipped and doesn't attempt to subscribe

            var client = new CentrifugeClient(ServerUrl);
            var connectedEvent = new TaskCompletionSource<bool>();

            client.Connected += (s, e) => connectedEvent.TrySetResult(true);

            try
            {
                // Connect first
                client.Connect();
                await connectedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var sub1 = client.NewSubscription("test_batch_unsub_1");
                var sub2 = client.NewSubscription("test_batch_unsub_2");
                var sub3 = client.NewSubscription("test_batch_unsub_3");

                var sub1Subscribed = new TaskCompletionSource<bool>();
                var sub3Subscribed = new TaskCompletionSource<bool>();

                sub1.Subscribed += (s, e) => sub1Subscribed.TrySetResult(true);
                sub3.Subscribed += (s, e) => sub3Subscribed.TrySetResult(true);

                // Subscribe to all three
                sub1.Subscribe();
                sub2.Subscribe();
                sub3.Subscribe();

                // Immediately unsubscribe sub2 (before batch flushes)
                sub2.Unsubscribe();

                // Wait for sub1 and sub3 to subscribe
                await Task.WhenAll(sub1Subscribed.Task, sub3Subscribed.Task).WaitAsync(TimeSpan.FromSeconds(5));

                // Verify states
                Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub1.State);
                Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub2.State);  // Should stay unsubscribed
                Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub3.State);

                // Cleanup
                sub1.Unsubscribe();
                sub3.Unsubscribe();
                client.Disconnect();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }
}
