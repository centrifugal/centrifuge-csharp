using System;
using System.Linq;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Unsubscribe() while the client is still connecting must not send an
    /// unsubscribe command. There is no server-side session to remove the
    /// subscription from, and the command would sit in the batch until the connect
    /// completes — past the per-command timeout when the connect is slow — where
    /// its failure is treated as an unsubscribe error and forces a needless
    /// reconnect. Mirrors centrifuge-js, whose _unsubscribe returns early when the
    /// transport is not open.
    /// </summary>
    [Collection("Integration")]
    public class UnsubscribeWhileConnectingTests : IAsyncLifetime
    {
        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public Task InitializeAsync() => _server.StartAsync();

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        [Fact]
        public async Task UnsubscribeWhileTransportOpeningSendsNothingAndKeepsConnecting()
        {
            // Hold the WebSocket handshake so the client stays in Connecting with a
            // transport that exists but is not open yet.
            var handshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.BeforeAccept = () =>
            {
                handshakeStarted.TrySetResult();
                return releaseHandshake.Task;
            };

            try
            {
                var connecting = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeConnectingEventArgs>();
                _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions
                {
                    // Short per-command timeout so a queued unsubscribe would fail quickly.
                    Timeout = TimeSpan.FromMilliseconds(300),
                });
                _client.Connecting += (_, e) => connecting.Writer.TryWrite(e);

                var sub = _client.NewSubscription("news");
                sub.Subscribe();
                _client.Connect();
                await handshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(CentrifugeClientState.Connecting, _client.State);

                sub.Unsubscribe();
                Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);

                // Well past the command timeout: a queued unsubscribe would have failed
                // by now and dragged the client through a reconnect.
                await Task.Delay(1000);

                var first = await connecting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(CentrifugeConnectingCodes.ConnectCalled, first.Code);
                Assert.False(connecting.Reader.TryRead(out var extra),
                    $"unexpected Connecting event: code={extra?.Code} reason='{extra?.Reason}'");
                Assert.Equal(CentrifugeClientState.Connecting, _client.State);

                // Let the handshake through: the client connects normally and the server
                // never sees a subscribe or an unsubscribe for the abandoned subscription.
                releaseHandshake.TrySetResult();
                await _client.ReadyAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(200);
                Assert.DoesNotContain(_server.Received, c => c.Unsubscribe != null);
                Assert.DoesNotContain(_server.Received, c => c.Subscribe != null);
                Assert.Equal(CentrifugeSubscriptionState.Unsubscribed, sub.State);
            }
            finally
            {
                releaseHandshake.TrySetResult();
            }
        }
    }
}
