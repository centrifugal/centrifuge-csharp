using System;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Tests for CentrifugeClient.
    /// </summary>
    public class ClientTests
    {
        [Fact]
        public void Client_Constructor_ThrowsOnEmptyEndpoint()
        {
            Assert.Throws<ArgumentException>(() => new CentrifugeClient(""));
        }

        [Fact]
        public void Client_Constructor_ThrowsOnNullEndpoint()
        {
            Assert.Throws<ArgumentNullException>(() => new CentrifugeClient((string)null!));
        }

        [Fact]
        public void Client_InitialState_IsDisconnected()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            Assert.Equal(ClientState.Disconnected, client.State);
        }

        [Fact]
        public async Task Client_Connect_ChangesStateToConnecting()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            var stateChangedTcs = new TaskCompletionSource<bool>();

            client.StateChanged += (sender, args) =>
            {
                if (args.NewState == ClientState.Connecting)
                {
                    stateChangedTcs.TrySetResult(true);
                }
            };

            // This will fail to connect since there's no server, but state should change
            client.Connect();

            // Wait for state change event with timeout
            var stateChanged = await stateChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(stateChanged);
        }

        [Fact]
        public void Client_NewSubscription_CreatesSubscription()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            var sub = client.NewSubscription("test");

            Assert.NotNull(sub);
            Assert.Equal("test", sub.Channel);
            Assert.Equal(SubscriptionState.Unsubscribed, sub.State);
        }

        [Fact]
        public void Client_NewSubscription_ThrowsOnDuplicate()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            client.NewSubscription("test");

            Assert.Throws<InvalidOperationException>(() => client.NewSubscription("test"));
        }

        [Fact]
        public void Client_GetSubscription_ReturnsNullForNonexistent()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            var sub = client.GetSubscription("nonexistent");

            Assert.Null(sub);
        }

        [Fact]
        public void Client_GetSubscription_ReturnsExisting()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            var created = client.NewSubscription("test");
            var retrieved = client.GetSubscription("test");

            Assert.Same(created, retrieved);
        }

        [Fact]
        public void Client_Disconnect_ChangesStateToDisconnected()
        {
            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
            client.Disconnect();

            Assert.Equal(ClientState.Disconnected, client.State);
        }

        // Integration tests require a running Centrifugo server
        // These would be similar to the JavaScript tests in centrifuge.test.ts
        //
        // Example structure for integration tests:
        //
        // [Fact]
        // public async Task Client_ConnectsAndDisconnects()
        // {
        //     var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");
        //     var connectedEvent = new TaskCompletionSource<bool>();
        //     var disconnectedEvent = new TaskCompletionSource<bool>();
        //
        //     client.Connected += (s, e) => connectedEvent.SetResult(true);
        //     client.Disconnected += (s, e) => disconnectedEvent.SetResult(true);
        //
        //     client.Connect(); await client.ReadyAsync();
        //     await connectedEvent.Task.WithTimeout(TimeSpan.FromSeconds(5));
        //     Assert.Equal(ClientState.Connected, client.State);
        //
        //     client.Disconnect();
        //     await disconnectedEvent.Task.WithTimeout(TimeSpan.FromSeconds(5));
        //     Assert.Equal(ClientState.Disconnected, client.State);
        // }
    }

    /// <summary>
    /// Tests for options validation.
    /// </summary>
    public class OptionsTests
    {
        [Fact]
        public void ClientOptions_Validate_ThrowsOnNegativeMinDelay()
        {
            var options = new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(-1)
            };

            Assert.Throws<ConfigurationException>(() => options.Validate());
        }

        [Fact]
        public void ClientOptions_Validate_ThrowsOnMaxLessThanMin()
        {
            var options = new CentrifugeClientOptions
            {
                MinReconnectDelay = TimeSpan.FromMilliseconds(1000),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(500)
            };

            Assert.Throws<ConfigurationException>(() => options.Validate());
        }

        [Fact]
        public void ClientOptions_Validate_ThrowsOnNegativeTimeout()
        {
            var options = new CentrifugeClientOptions
            {
                Timeout = TimeSpan.FromMilliseconds(-1)
            };

            Assert.Throws<ConfigurationException>(() => options.Validate());
        }

        [Fact]
        public void SubscriptionOptions_Validate_ThrowsOnNegativeMinDelay()
        {
            var options = new SubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(-1)
            };

            Assert.Throws<ConfigurationException>(() => options.Validate());
        }
    }
}
