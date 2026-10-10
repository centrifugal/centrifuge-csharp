using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Behaviours of the SDK specification (TLA+ SDK spec) and centrifuge-js the client follows, against the
    /// in-process fake server.
    /// </summary>
    [Collection("Integration")]
    public class SpecConformanceTests : IAsyncLifetime
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;

        public Task InitializeAsync() => _server.StartAsync();

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        private CentrifugeClient NewClient(CentrifugeClientOptions? options = null)
        {
            options ??= new CentrifugeClientOptions();
            options.MinReconnectDelay = TimeSpan.FromMilliseconds(1);
            options.MaxReconnectDelay = TimeSpan.FromMilliseconds(50);
            return _client = new CentrifugeClient(_server.Url, options);
        }

        private static async Task Until(Func<bool> condition)
        {
            await Task.Run(async () =>
            {
                while (!condition()) await Task.Delay(10);
            }).WaitAsync(Wait);
        }

        /// <summary>
        /// A subscribe error 109 (token expired) without GetToken: a configuration error, then unsubscribed
        /// as unauthorized — not a resubscribe without a token (centrifuge-js _subscribeError).
        /// </summary>
        [Fact]
        public async Task ExpiredSubscriptionTokenWithoutGetTokenUnsubscribesUnauthorized()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null
                : new Reply { Id = cmd.Id, Error = new Error { Code = 109, Message = "token expired" } };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("expired", new CentrifugeSubscriptionOptions { Token = "static" });
            var errors = new List<string>();
            var unsubscribed = new TaskCompletionSource<CentrifugeUnsubscribedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Error += (_, e) => { lock (errors) errors.Add(e.Type); };
            sub.Unsubscribed += (_, e) => unsubscribed.TrySetResult(e);
            sub.Subscribe();

            var args = await unsubscribed.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeUnsubscribedCodes.Unauthorized, args.Code);
            lock (errors) Assert.Equal(new[] { "configuration" }, errors);
            Assert.Equal(1, _server.Received.Count(c => c.Subscribe != null));
        }

        /// <summary>
        /// Unsubscribe() ends the subscribe flow in progress: a following Subscribe() starts a new one at
        /// once, without waiting for the GetToken of the old one, whose result is ignored (TLA Unsubscribed:
        /// infl = FALSE, a new flow).
        /// </summary>
        [Fact]
        public async Task SubscribeAfterUnsubscribeDoesNotWaitForOldGetToken()
        {
            var stuck = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("stuck-token", new CentrifugeSubscriptionOptions
            {
                GetToken = _ => Interlocked.Increment(ref calls) == 1 ? stuck.Task : Task.FromResult("fresh"),
            });
            sub.Subscribe();
            await Until(() => Volatile.Read(ref calls) == 1);

            sub.Unsubscribe();
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal("fresh", _server.LastSubscribe()!.Token);

            // The old flow's token arrives late: it sends nothing.
            stuck.TrySetResult("stale");
            await client.RpcAsync("barrier", default).WaitAsync(Wait);
            Assert.Equal(1, _server.Received.Count(c => c.Subscribe != null));
        }

        /// <summary>
        /// The events of one transition are never interleaved with another one's: a Disconnect() from
        /// another thread while a no-ping teardown runs leaves the app with a consistent sequence, and the
        /// last StateChanged matches State.
        /// </summary>
        [Fact]
        public async Task ConcurrentTeardownsRaiseConsistentEvents()
        {
            for (var i = 0; i < 30; i++)
            {
                var client = NewClient(new CentrifugeClientOptions
                {
                    MinReconnectDelay = TimeSpan.FromSeconds(60),
                    MaxReconnectDelay = TimeSpan.FromSeconds(60),
                });
                client.Connect();
                await client.ReadyAsync(Wait);
                var states = new List<CentrifugeClientState>();
                client.StateChanged += (_, e) => { lock (states) states.Add(e.NewState); };
                var transport = NoPing.Transport(client)!;

                await Task.WhenAll(Task.Run(() => client.NoPing(transport)), Task.Run(client.Disconnect));
                await client.LoopBarrierAsync().WaitAsync(Wait);

                lock (states)
                {
                    Assert.Equal(CentrifugeClientState.Disconnected, client.State);
                    Assert.Equal(client.State, states.Last());
                }
                await client.DisposeAsync();
            }
        }

        /// <summary>
        /// A call from another thread while a handler runs on the client's loop doesn't wait for it: it takes
        /// effect right after the handler, in call order. Waiting would deadlock a handler that marshals to
        /// that thread synchronously (Control.Invoke while the UI thread calls the SDK), or two clients whose
        /// handlers call each other.
        /// </summary>
        [Fact]
        public async Task CallFromAnotherThreadDoesNotWaitForHandler()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("from-thread");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            var inHandler = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            client.Message += (_, _) =>
            {
                inHandler.Set();
                // Waits for the test thread, as a handler marshalling synchronously to it would.
                release.Wait(Wait);
            };
            var unsubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribed.TrySetResult(true);
            await _server.SendPushAsync(new Push { Message = new Message() });
            Assert.True(inHandler.Wait(Wait));

            sub.Unsubscribe();
            sub.Subscribe();
            Assert.False(unsubscribed.Task.IsCompleted, "the call ran while the handler held the loop");
            release.Set();

            await unsubscribed.Task.WaitAsync(Wait);
            await sub.ReadyAsync(Wait);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
        }

        /// <summary>
        /// A handler blocking on a task already decided when it asks (ReadyAsync while connected) gets it at
        /// once: blocking in handlers is unsupported, but it worked before and must not hang the client.
        /// </summary>
        [Fact]
        public async Task DecidedTaskFromHandlerCompletesAtOnce()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("decided");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            var results = new TaskCompletionSource<(bool Client, bool Sub)>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Message += (_, _) => results.TrySetResult((
                client.ReadyAsync().Wait(TimeSpan.FromSeconds(2)),
                sub.ReadyAsync().Wait(TimeSpan.FromSeconds(2))));
            await _server.SendPushAsync(new Push { Message = new Message() });

            var (clientReady, subReady) = await results.Task.WaitAsync(Wait);
            Assert.True(clientReady);
            Assert.True(subReady);
        }

        /// <summary>
        /// A subscription subscribed before the connect reply (its subscribe goes with the connect) moves back to
        /// subscribing when that connect fails, and subscribes again on the next connection (TLA Teardown moves
        /// every subscribed subscription; centrifuge-js only does so for a connected client).
        /// </summary>
        [Fact]
        public async Task SubscriptionSubscribedBeforeFailedConnectResubscribes()
        {
            var connects = 0;
            _server.OnCommand = cmd => cmd.Connect != null && Interlocked.Increment(ref connects) == 1 ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient(new CentrifugeClientOptions { Timeout = TimeSpan.FromMilliseconds(500) });
            var sub = client.NewSubscription("early");
            var subscribed = 0;
            var subscribing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Subscribed += (_, _) => Interlocked.Increment(ref subscribed);
            sub.Subscribing += (_, e) => { if (Volatile.Read(ref subscribed) == 1) subscribing.TrySetResult(true); };
            sub.Subscribe();
            client.Connect();

            await subscribing.Task.WaitAsync(Wait);
            await client.ReadyAsync(Wait);
            await Until(() => Volatile.Read(ref subscribed) == 2);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.Equal(2, _server.Received.Count(c => c.Subscribe != null));
        }

        /// <summary>
        /// A refresh reply without its result breaks the protocol: a temporary refresh error, retried — not a
        /// success that silently stops refreshing the token.
        /// </summary>
        [Fact]
        public async Task RefreshReplyWithoutResultIsRefreshError()
        {
            _server.ConnectResult = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25, Expires = true, Ttl = 1 };
            _server.OnCommand = cmd => cmd.Refresh != null ? new Reply { Id = cmd.Id } : null;
            var client = NewClient(new CentrifugeClientOptions { Token = "t", GetToken = () => Task.FromResult("t2") });
            var refreshError = new TaskCompletionSource<CentrifugeErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (_, e) => { if (e.Type == "refresh") refreshError.TrySetResult(e); };
            client.Connect();
            await client.ReadyAsync(Wait);

            var error = await refreshError.Task.WaitAsync(Wait);
            Assert.True(error.Temporary);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>
        /// An emulation request answered with a status the server gave for that request only (e.g. 413) fails
        /// its commands but keeps the session; no response, 404 (no node of the session) and 5xx close the
        /// transport (centrifuge-js http_stream).
        /// </summary>
        [Theory]
        [InlineData(413, false)]
        [InlineData(400, false)]
        [InlineData(404, true)]
        [InlineData(502, true)]
        public void EmulationRequestFailureClosesTransportOnlyWhenSessionIsUnreachable(int status, bool closes)
        {
            var error = new CentrifugeException(CentrifugeErrorCodes.TransportWriteError, "write", true, new EmulationStatusException(status));
            Assert.Equal(closes, CentrifugeClient.EmulationTransportFailed(error));
            Assert.True(CentrifugeClient.EmulationTransportFailed(new System.Net.Http.HttpRequestException("connection refused")));
        }
    }
}
