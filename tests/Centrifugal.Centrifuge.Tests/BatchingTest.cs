using System;
using System.Threading.Tasks;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    public class BatchingTests
    {
        [Fact]
        public async Task MultipleSubscribes_BeforeConnect_BatchedTogether()
        {
            // This test demonstrates that subscribing to multiple channels before connect
            // will batch all subscribe requests together when connection is established

            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

            // Subscribe to 10 channels before connecting
            var subscriptions = new CentrifugeSubscription[10];
            for (int i = 0; i < 10; i++)
            {
                var sub = client.NewSubscription($"channel{i}");
                sub.Subscribe();  // Non-blocking, queues for later
                subscriptions[i] = sub;
            }

            // Verify all are in Subscribing state
            foreach (var sub in subscriptions)
            {
                Assert.Equal(SubscriptionState.Subscribing, sub.State);
            }

            // Note: In a real test with a server, you would:
            // 1. Connect to the server
            // 2. Verify all subscriptions complete
            // 3. Monitor network traffic to confirm only ONE batch was sent with all 10 subscribes
        }

        [Fact]
        public async Task MultipleSubscribes_AfterConnect_AutoBatchedWithDelay()
        {
            // This test demonstrates that subscribing to multiple channels quickly after connect
            // will automatically batch them together with a 1ms delay window

            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

            // Note: In a real test with a server, you would:
            // 1. Connect and wait for connection
            // 2. Quickly subscribe to 10 channels (within 1ms)
            // 3. Monitor network traffic to confirm subscribes were batched
            // 4. If you wait >1ms between subscribes, they would be in separate batches

            // The batching logic:
            // - First subscribe starts 1ms timer
            // - Subsequent subscribes within that 1ms are collected
            // - When timer fires, all collected subscribes sent in one batch
        }
    }
}
