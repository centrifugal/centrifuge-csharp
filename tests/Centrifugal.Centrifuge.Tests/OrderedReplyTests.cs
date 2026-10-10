using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Centrifugal.Centrifuge.Transports;
using Google.Protobuf;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// How connect and subscribe replies are ordered against the rest of the frame they
    /// arrive in, and how each asynchronous result (a reply or error reply, a GetState or
    /// GetToken result, a token refresh, a failed connect) stays tied to the connect or
    /// subscribe attempt that started it.
    /// <para>
    /// A test that checks something did not happen waits for the point where it would
    /// have happened, never for a fixed delay: a later command arrived at the server
    /// (the commands of a session arrive in the order they were queued), a later frame
    /// was dispatched, the client's transport handler took the frame
    /// (<see cref="FrameProcessed"/>), an SDK call made by the test returned, or a
    /// source owned by the test was completed inline (<see cref="CompleteInline"/>).
    /// A command timer has no such point, so the test waits it out on a clock
    /// (<see cref="TimeoutHold"/>). <see cref="Wait"/> is just the upper bound.
    /// </para>
    /// <para>
    /// Frame gate (<see cref="FrameGate"/>): a handler raised while a reply is applied
    /// either runs inside the event loop step of that frame, so the rest of the frame
    /// waits for it and keeps protocol order; or it runs elsewhere, and then it waits
    /// for the competing event of the frame, which gets in first and spoils the
    /// recorded order.
    /// </para>
    /// </summary>
    [Collection("Integration")]
    public class OrderedReplyTests : IAsyncLifetime
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
        /// <summary>Resubscribe delay well beyond Wait: a test only passes if the SDK retries without backoff.</summary>
        private static readonly TimeSpan NoBackoff = TimeSpan.FromSeconds(30);
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly FakeCentrifugoServer _server = new();
        private CentrifugeClient? _client;
        private readonly List<string> _order = new();
        private int _frameThread;

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

        private static CentrifugeSubscriptionOptions WithoutBackoff() => new()
        {
            MinResubscribeDelay = NoBackoff,
            MaxResubscribeDelay = NoBackoff,
        };

        private void Record(string entry)
        {
            lock (_order) _order.Add(entry);
        }

        private string[] Recorded()
        {
            lock (_order) return _order.ToArray();
        }

        private static Publication Pub(ulong offset, string data) =>
            new() { Offset = offset, Data = ByteString.CopyFromUtf8(data) };

        private static Publication DeltaPub(ulong offset, string delta) =>
            new() { Offset = offset, Data = ByteString.CopyFromUtf8(delta), Delta = true };

        /// <summary>Leaves the first subscribe command without a reply and gives it to the test.</summary>
        private TaskCompletionSource<Command> HoldFirstSubscribe()
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Subscribe != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            return held;
        }

        private static async Task Until(Func<bool> condition)
        {
            await Task.Run(async () =>
            {
                while (!condition()) await Task.Delay(10);
            }).WaitAsync(Wait);
        }

        private static object? Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner);

        private static ITransport CurrentTransport(CentrifugeClient client) => (ITransport)Field(client, "_transport")!;

        /// <summary>
        /// Completes once the current transport delivered a frame and the client's own handler,
        /// subscribed before this one, handed it to the event loop (ran or queued its step).
        /// </summary>
        private static Task FrameProcessed(CentrifugeClient client)
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CurrentTransport(client).MessageReceived += (_, _) => processed.TrySetResult();
            return processed.Task;
        }

        /// <summary>
        /// Completes a source owned by the test (made without RunContinuationsAsynchronously) on
        /// a pool thread, away from the test's synchronization context. The SDK awaits it with
        /// ConfigureAwait(false), so its continuation runs inline, and by the time this returns
        /// the SDK has gone on to its next pending await. The SDK must be awaiting the source
        /// already (see UntilAwaited).
        /// </summary>
        private static Task CompleteInline(Action complete) => Task.Run(complete);

        /// <summary>
        /// Waits for <paramref name="task"/> to get a continuation: the SDK awaits a source it
        /// was given only after the call that returned the source.
        /// </summary>
        private static Task UntilAwaited(Task task) => Until(() => TaskContinuation.GetValue(task) != null);

        private static readonly FieldInfo TaskContinuation = typeof(Task).GetField("m_continuationObject", Private)!;

        /// <summary>First message of a frame: its Message event records the thread that runs the frame's step.</summary>
        private static Reply FrameMarker() =>
            new() { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("frame") } } };

        private void TrackFrameThread(CentrifugeClient client) =>
            client.Message += (_, _) => Volatile.Write(ref _frameThread, Environment.CurrentManagedThreadId);

        /// <summary>
        /// Waits for <paramref name="competing"/> unless called on the thread running the step of
        /// the frame that <see cref="FrameMarker"/> opened. A thread that already finished that
        /// step has raised the competing event too, and the recorded order shows it.
        /// </summary>
        private void FrameGate(ManualResetEventSlim competing)
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _frameThread)) competing.Wait(Wait);
        }

        /// <summary>Command timeout for the tests that hold a reply past it: long enough for a loopback reply to arrive first.</summary>
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Keeps the event loop busy, from a handler raised in the step of a reply's frame, until a
        /// command's timer has run out. It is created before the command is issued, so by its clock
        /// the timer can't fire sooner than <see cref="CommandTimeout"/> minus <see cref="TimerSlack"/>
        /// (timer tick granularity); a handler entered before that ran ahead of the timer, which is
        /// asserted. The hold ends at the timer's due time, counted from when the server got the
        /// command (<see cref="Armed"/>), plus a margin for the timer's steps to run once it fires.
        /// </summary>
        private sealed class TimeoutHold
        {
            private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);
            private static readonly TimeSpan TimerReaction = TimeSpan.FromMilliseconds(500);
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly TaskCompletionSource<TimeSpan> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _released = new();
            private TimeSpan? _armed;

            /// <summary>Marks the moment the server got the command.</summary>
            public void Armed() => _armed = _clock.Elapsed;

            /// <summary>Called from the handler inside the loop: blocks it until <see cref="Release"/>.</summary>
            public void Hold()
            {
                _entered.TrySetResult(_clock.Elapsed);
                _released.Wait(Wait);
            }

            /// <summary>Completes once the handler, entered before the timer fired, has kept the loop busy past it.</summary>
            public async Task PastTimeoutAsync()
            {
                var entered = await _entered.Task.WaitAsync(Wait);
                Assert.True(entered < CommandTimeout - TimerSlack,
                    $"Reply claimed {entered.TotalMilliseconds:F0} ms after the command was issued, not before its timeout");
                TimeSpan left;
                while ((left = _armed!.Value + CommandTimeout + TimerReaction - _clock.Elapsed) > TimeSpan.Zero)
                    await Task.Delay(left);
            }

            public void Release() => _released.Set();
        }

        /// <summary>Frame gate at the Subscribed transition; competing event: the live publication.</summary>
        [Fact]
        public async Task LivePublicationDoesNotOvertakeSubscribedAndRecovered()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            TrackFrameThread(client);
            client.Connect();
            await client.ReadyAsync(Wait);

            var liveSeen = new ManualResetEventSlim();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sub = client.NewSubscription("ordered");
            sub.StateChanged += (_, e) =>
            {
                if (e.NewState != CentrifugeSubscriptionState.Subscribed) return;
                Record("state");
                FrameGate(liveSeen);
            };
            sub.Subscribed += (_, _) => Record("subscribed");
            sub.Publication += (_, e) =>
            {
                Record("pub" + e.Offset);
                if (e.Offset != 3) return;
                liveSeen.Set();
                done.TrySetResult();
            };
            sub.Subscribe();

            var cmd = await held.Task.WaitAsync(Wait);
            await _server.SendRepliesAsync(
                FrameMarker(),
                new Reply
                {
                    Id = cmd.Id,
                    Subscribe = new SubscribeResult
                    {
                        Recoverable = true, Positioned = true, Epoch = "e", Offset = 2, Recovered = true,
                        Publications = { Pub(1, "r1"), Pub(2, "r2") },
                    },
                },
                new Reply { Push = new Push { Channel = "ordered", Pub = Pub(3, "live") } });

            await done.Task.WaitAsync(Wait);
            Assert.Equal(new[] { "state", "subscribed", "pub1", "pub2", "pub3" }, Recorded());
        }

        /// <summary>Frame gate at the Subscribed transition; competing event: the live delta.</summary>
        [Fact]
        public async Task LiveDeltaDecodedAfterRecoveredBase()
        {
            const string delta = "B\n6@0,5:therexz4NK;";
            Assert.Equal("hello there", Encoding.UTF8.GetString(
                Fossil.ApplyDelta(Encoding.UTF8.GetBytes("hello world"), Encoding.UTF8.GetBytes(delta))));

            var held = HoldFirstSubscribe();
            var client = NewClient();
            TrackFrameThread(client);
            client.Connect();
            await client.ReadyAsync(Wait);

            var liveSeen = new ManualResetEventSlim();
            var live = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sub = client.NewSubscription("delta", new CentrifugeSubscriptionOptions { Delta = "fossil" });
            sub.StateChanged += (_, e) =>
            {
                if (e.NewState == CentrifugeSubscriptionState.Subscribed) FrameGate(liveSeen);
            };
            sub.Publication += (_, e) =>
            {
                if (e.Offset != 2) return;
                liveSeen.Set();
                live.TrySetResult(Encoding.UTF8.GetString(e.Data.Span));
            };
            sub.Subscribe();

            var cmd = await held.Task.WaitAsync(Wait);
            await _server.SendRepliesAsync(
                FrameMarker(),
                new Reply
                {
                    Id = cmd.Id,
                    Subscribe = new SubscribeResult
                    {
                        Recoverable = true, Positioned = true, Epoch = "e", Offset = 1, Recovered = true, Delta = true,
                        Publications = { Pub(1, "hello world") },
                    },
                },
                new Reply { Push = new Push { Channel = "delta", Pub = DeltaPub(2, delta) } });

            Assert.Equal("hello there", await live.Task.WaitAsync(Wait));
        }

        /// <summary>
        /// With delta enabled, only publications marked as deltas are decoded against the previous
        /// data. A recovered one that can't be decoded raises Error, stops the delivery and
        /// disconnects the client with BadProtocol.
        /// </summary>
        [Fact]
        public async Task RecoveredDeltaDecodesFlaggedOnlyAndReportsCorruption()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var disconnected = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, e) => disconnected.TrySetResult(e);

            var sub = client.NewSubscription("delta-corrupt", new CentrifugeSubscriptionOptions { Delta = "fossil" });
            var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, e) => Record(Encoding.UTF8.GetString(e.Data.Span));
            sub.Error += (_, e) =>
            {
                Record("error:" + e.Type);
                error.TrySetResult();
            };
            sub.Subscribe();

            var cmd = await held.Task.WaitAsync(Wait);
            await _server.SendRepliesAsync(new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult
                {
                    Recoverable = true, Positioned = true, Epoch = "e", Offset = 3, Recovered = true, Delta = true,
                    Publications = { Pub(1, "hello world"), Pub(2, "plain"), DeltaPub(3, "not a delta") },
                },
            });

            await error.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeDisconnectedCodes.BadProtocol, (await disconnected.Task.WaitAsync(Wait)).Code);
            Assert.Equal(new[] { "hello world", "plain", "error:publicationDecode" }, Recorded());
        }

        /// <summary>
        /// Channel compaction: a push in the same frame as the subscribe reply, addressed by
        /// the numeric id that reply assigns, must be delivered. No gate: handled before the
        /// reply is applied, the id would be unknown and the push dropped, which happens often
        /// enough to show up within the iterations.
        /// </summary>
        [Fact]
        public async Task CompactedPushInSameFrameAsReplyIsNotDropped()
        {
            _server.OnCommandFrame = cmd => cmd.Subscribe == null ? null : new[]
            {
                new Reply { Id = cmd.Id, Subscribe = new SubscribeResult { Id = 42 } },
                new Reply { Push = new Push { Id = 42, Pub = Pub(0, "x") } },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("compacted");
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, _) => Volatile.Read(ref received).TrySetResult();

            const int iterations = 20;
            for (var i = 0; i < iterations; i++)
            {
                Volatile.Write(ref received, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CentrifugeUnsubscribedEventArgs> onUnsubscribed = (_, _) => unsubscribed.TrySetResult();
                sub.Subscribe();
                await Volatile.Read(ref received).Task.WaitAsync(Wait);
                sub.Unsubscribed += onUnsubscribed;
                sub.Unsubscribe();
                await unsubscribed.Task.WaitAsync(Wait);
                sub.Unsubscribed -= onUnsubscribed;
            }
        }

        /// <summary>
        /// A live publication right after the subscribe reply moves the recovery position
        /// forward, and applying the reply late must not move it back. No gate: every reply
        /// has an even offset and is followed by the next, odd, publication, so each
        /// resubscribe has to recover from an odd offset. The message at the end of the frame
        /// is the barrier: it is dispatched after the publication.
        /// </summary>
        [Fact]
        public async Task StreamPositionNotRolledBackBySameFrameReply()
        {
            ulong next = 10;
            _server.OnCommandFrame = cmd =>
            {
                if (cmd.Subscribe == null) return null;
                var offset = next;
                next += 2;
                return new[]
                {
                    new Reply
                    {
                        Id = cmd.Id,
                        Subscribe = new SubscribeResult
                        {
                            Recoverable = true, Positioned = true, Epoch = "e", Offset = offset, WasRecovering = true, Recovered = true,
                        },
                    },
                    new Reply { Push = new Push { Channel = "position", Pub = Pub(offset + 1, "live") } },
                    new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("barrier") } } },
                };
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("position", new CentrifugeSubscriptionOptions
            {
                Since = new CentrifugeStreamPosition(5, "e"),
            });
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Message += (_, _) => Volatile.Read(ref barrier).TrySetResult();

            const int iterations = 30;
            for (var i = 0; i < iterations; i++)
            {
                Volatile.Write(ref barrier, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CentrifugeUnsubscribedEventArgs> onUnsubscribed = (_, _) => unsubscribed.TrySetResult();
                sub.Subscribe();
                await Volatile.Read(ref barrier).Task.WaitAsync(Wait);
                sub.Unsubscribed += onUnsubscribed;
                sub.Unsubscribe();
                await unsubscribed.Task.WaitAsync(Wait);
                sub.Unsubscribed -= onUnsubscribed;
            }

            var offsets = _server.Received.Where(c => c.Subscribe != null).Skip(1).Select(c => c.Subscribe.Offset).ToList();
            var rolledBack = offsets.Where(o => o % 2 == 0).ToList();
            Assert.True(rolledBack.Count == 0,
                $"position rolled back in {rolledBack.Count}/{offsets.Count} resubscribes: {string.Join(",", offsets)}");
        }

        /// <summary>
        /// A channel with no stream at subscribe time takes its epoch from the first publication,
        /// and the resubscribe recovers from that epoch (as centrifuge-js).
        /// </summary>
        [Fact]
        public async Task PublicationEpochBecomesRecoveryEpoch()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null : new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "", Offset = 0 },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("late-stream", new CentrifugeSubscriptionOptions { Recoverable = true });
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, _) => delivered.TrySetResult();
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            await _server.SendPushAsync(new Push { Channel = "late-stream", Pub = new Publication { Offset = 1, Epoch = "E1", Data = ByteString.CopyFromUtf8("first") } });
            await delivered.Task.WaitAsync(Wait);
            NoPing.Fire(client);
            await Until(() => _server.Received.Count(c => c.Subscribe != null) == 2);

            var resubscribe = _server.Received.Last(c => c.Subscribe != null).Subscribe;
            Assert.True(resubscribe.Recover);
            Assert.Equal("E1", resubscribe.Epoch);
            Assert.Equal(1UL, resubscribe.Offset);
        }

        /// <summary>
        /// When the publication handler resubscribes, the new subscribe recovers from after that
        /// publication, so it isn't delivered again (as centrifuge-js _pendingOffset).
        /// </summary>
        [Fact]
        public async Task ResubscribeFromPublicationHandlerRecoversPastIt()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null : new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e", Offset = 5 },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("resubscribe-in-handler", new CentrifugeSubscriptionOptions { Recoverable = true });
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Publication += (_, _) =>
            {
                sub.Unsubscribe();
                sub.Subscribe();
                handled.TrySetResult();
            };

            await _server.SendPushAsync(new Push { Channel = "resubscribe-in-handler", Pub = Pub(6, "sixth") });
            await handled.Task.WaitAsync(Wait);
            await Until(() => _server.Received.Count(c => c.Subscribe != null) == 2);

            var resubscribe = _server.Received.Last(c => c.Subscribe != null).Subscribe;
            Assert.True(resubscribe.Recover);
            Assert.Equal(6UL, resubscribe.Offset);
        }

        /// <summary>
        /// A channel that is positioned but not recoverable doesn't request recovery when it
        /// resubscribes (as centrifuge-js).
        /// </summary>
        [Fact]
        public async Task PositionedOnlySubscriptionDoesNotRecover()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null : new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult { Positioned = true, Epoch = "e", Offset = 5 },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("positioned", new CentrifugeSubscriptionOptions { Positioned = true });
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            NoPing.Fire(client);
            await Until(() => _server.Received.Count(c => c.Subscribe != null) == 2);

            Assert.False(_server.Received.Last(c => c.Subscribe != null).Subscribe.Recover);
        }

        /// <summary>Frame gate at the first Connected; competing event: the live publication.</summary>
        [Fact]
        public async Task ServerSideRecoveredPublicationsPrecedeLiveAndPositionAdvances()
        {
            var firstConnect = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && firstConnect.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;

            var client = NewClient();
            TrackFrameThread(client);
            var liveSeen = new ManualResetEventSlim();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = 0;
            client.Connected += (_, _) =>
            {
                Record("connected");
                if (Interlocked.Increment(ref connected) == 1) FrameGate(liveSeen);
            };
            client.ServerSubscribed += (_, _) => Record("server-subscribed");
            client.Publication += (_, e) =>
            {
                Record("pub" + e.Offset);
                if (e.Offset != 3) return;
                liveSeen.Set();
                done.TrySetResult();
            };
            client.Connect();

            var cmd = await firstConnect.Task.WaitAsync(Wait);
            var result = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            result.Subs.Add("ss", new SubscribeResult
            {
                Recoverable = true, Positioned = true, Epoch = "e", Offset = 2, Recovered = true,
                Publications = { Pub(1, "r1"), Pub(2, "r2") },
            });
            await _server.SendRepliesAsync(
                FrameMarker(),
                new Reply { Id = cmd.Id, Connect = result },
                new Reply { Push = new Push { Channel = "ss", Pub = Pub(3, "live") } });
            await done.Task.WaitAsync(Wait);
            Assert.Equal(new[] { "connected", "server-subscribed", "pub1", "pub2", "pub3" }, Recorded().Take(5));

            _server.CloseConnection();
            await Until(() => _server.Received.Count(c => c.Connect != null) == 2);
            Assert.Equal(3UL, _server.Received.Last(c => c.Connect != null).Connect.Subs["ss"].Offset);
        }

        /// <summary>The publications in the reply of an unrecovered server-side subscription aren't delivered, live ones are.</summary>
        [Fact]
        public async Task ServerSideUnrecoveredPublicationsAreNotDelivered()
        {
            var connect = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && connect.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;

            var client = NewClient();
            var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ServerSubscribed += (_, e) => Record("server-subscribed:" + e.Recovered);
            client.Publication += (_, e) =>
            {
                Record("pub" + e.Offset);
                if (e.Offset == 3) live.TrySetResult();
            };
            client.Connect();

            var cmd = await connect.Task.WaitAsync(Wait);
            var result = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            result.Subs.Add("ss", new SubscribeResult
            {
                Recoverable = true, Positioned = true, Epoch = "e", Offset = 2,
                Publications = { Pub(1, "r1"), Pub(2, "r2") },
            });
            await _server.SendRepliesAsync(
                new Reply { Id = cmd.Id, Connect = result },
                new Reply { Push = new Push { Channel = "ss", Pub = Pub(3, "live") } });
            await live.Task.WaitAsync(Wait);
            Assert.Equal(new[] { "server-subscribed:False", "pub3" }, Recorded());
        }

        /// <summary>
        /// Frame gate at the first Subscribed transition; competing events: the teardown caused
        /// by the Disconnect push and the Subscribed of the resubscribe after it.
        /// </summary>
        [Fact]
        public async Task TeardownRightAfterReplyIsOrderedAfterSubscribed()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            TrackFrameThread(client);
            client.Connect();
            await client.ReadyAsync(Wait);

            var subscribedSeen = new ManualResetEventSlim();
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gated = 0;
            var subscribedCount = 0;
            var sub = client.NewSubscription("teardown");
            sub.StateChanged += (_, e) =>
            {
                Record("state:" + e.NewState);
                if (e.NewState == CentrifugeSubscriptionState.Subscribed && Interlocked.Exchange(ref gated, 1) == 0)
                    FrameGate(subscribedSeen);
            };
            sub.Subscribing += (_, _) => Record("subscribing");
            sub.Subscribed += (_, _) =>
            {
                Record("subscribed");
                subscribedSeen.Set();
                if (Interlocked.Increment(ref subscribedCount) == 2) second.TrySetResult();
            };
            sub.Subscribe();

            var cmd = await held.Task.WaitAsync(Wait);
            Record("--reply--");
            await _server.SendRepliesAsync(
                FrameMarker(),
                new Reply { Id = cmd.Id, Subscribe = new SubscribeResult() },
                new Reply { Push = new Push { Disconnect = new Disconnect { Code = 3000, Reason = "go away", Reconnect = true } } });

            await second.Task.WaitAsync(Wait);
            Assert.Equal(new[]
            {
                "state:Subscribing", "subscribing", "--reply--",
                "state:Subscribed", "subscribed",
                "state:Subscribing", "subscribing",
                "state:Subscribed", "subscribed",
            }, Recorded());
        }

        [Fact]
        public async Task StaleErrorReplyDoesNotUnsubscribeNewAttempt()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var unsubscribed = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeUnsubscribedEventArgs>();
            var sub = client.NewSubscription("stale-error", WithoutBackoff());
            sub.Unsubscribed += (_, e) => unsubscribed.Writer.TryWrite(e);
            sub.Subscribe();

            var cmd = await held.Task.WaitAsync(Wait);
            sub.Unsubscribe();
            sub.Subscribe();
            Assert.Equal(CentrifugeUnsubscribedCodes.UnsubscribeCalled,
                (await unsubscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Code);

            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 103, Message = "permission denied" } });

            await sub.ReadyAsync(Wait);
            Assert.False(unsubscribed.Reader.TryRead(out var extra), $"stale error unsubscribed the new attempt: {extra?.Code}");
        }

        /// <summary>
        /// The server sees subscribe#1, then unsubscribe, then answers subscribe#1. It only ends
        /// up subscribed if the new attempt sent a subscribe of its own after that unsubscribe.
        /// </summary>
        [Fact]
        public async Task SupersededSuccessReplyIsNotAdoptedByNewAttempt()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("superseded", WithoutBackoff());
            sub.Subscribe();
            var cmd = await held.Task.WaitAsync(Wait);

            var unsubscribeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = _server.OnCommand!;
            _server.OnCommand = c =>
            {
                if (c.Unsubscribe != null) unsubscribeSeen.TrySetResult();
                return hold(c);
            };
            sub.Unsubscribe();
            sub.Subscribe();
            await unsubscribeSeen.Task.WaitAsync(Wait);
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Subscribe = new SubscribeResult() });

            await sub.ReadyAsync(Wait);
            var received = _server.Received.ToList();
            var unsubscribeIndex = received.FindIndex(c => c.Unsubscribe != null);
            Assert.Contains(received.Skip(unsubscribeIndex + 1), c => c.Subscribe != null);
        }

        [Fact]
        public async Task SupersededGetStateResultIsNotUsed()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<CentrifugeStreamPosition>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var options = WithoutBackoff();
            options.GetState = _ =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(new CentrifugeStreamPosition(200, "second"));
                firstCall.TrySetResult();
                return releaseFirst.Task;
            };
            var sub = client.NewSubscription("get-state", options);
            sub.Subscribe();
            await firstCall.Task.WaitAsync(Wait);

            sub.Unsubscribe();
            sub.Subscribe();
            releaseFirst.TrySetResult(new CentrifugeStreamPosition(100, "first"));

            await sub.ReadyAsync(Wait);
            var last = _server.LastSubscribe()!;
            Assert.Equal("second", last.Epoch);
            Assert.Equal(200UL, last.Offset);
        }

        /// <summary>
        /// The Unsubscribed handler of a disposed subscription may subscribe to the same channel
        /// again (as centrifuge-js): the disposed instance is removed from the client before its
        /// events run, and its unsubscribe reaches the server ahead of the new subscribe.
        /// </summary>
        [Fact]
        public async Task DisposedSubscriptionHandlerSubscribesChannelAnew()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("renew");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            CentrifugeSubscription? renewed = null;
            sub.Unsubscribed += (_, _) =>
            {
                renewed = client.NewSubscription("renew");
                renewed.Subscribe();
            };

            sub.Dispose();

            Assert.NotNull(renewed);
            Assert.Same(renewed, client.GetSubscription("renew"));
            await renewed!.ReadyAsync(Wait);
            var wire = _server.Received
                .Where(c => c.Subscribe != null || c.Unsubscribe != null)
                .Select(c => c.Subscribe != null ? "sub" : "unsub");
            Assert.Equal(new[] { "sub", "unsub", "sub" }, wire);
        }

        /// <summary>
        /// Client disposal removes every subscription before raising its Unsubscribed(ClientClosed)
        /// and leaves no subscription registered.
        /// </summary>
        [Fact]
        public async Task DisposedClientRemovesSubscriptionsBeforeTheirEvents()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("closing");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            var registered = true;
            int? code = null;
            sub.Unsubscribed += (_, e) =>
            {
                registered = client.GetSubscription("closing") != null;
                code = e.Code;
            };

            await client.DisposeAsync();

            Assert.False(registered);
            Assert.Equal(CentrifugeUnsubscribedCodes.ClientClosed, code);
            Assert.Empty(client.Subscriptions);
        }

        /// <summary>
        /// RemoveSubscription only removes the exact instance this client holds. A subscription to
        /// the same channel owned by another client stays registered and usable (ownership check),
        /// and removing a stale instance keeps the newer one for that channel (removal by instance).
        /// </summary>
        [Fact]
        public void RemoveSubscriptionLeavesInstancesNotHeld()
        {
            var client = NewClient();
            using var other = new CentrifugeClient(_server.Url);
            var foreign = other.NewSubscription("shared");
            var stale = client.NewSubscription("shared");
            stale.Dispose();
            var renewed = client.NewSubscription("shared");

            client.RemoveSubscription(foreign);
            client.RemoveSubscription(stale);

            Assert.Same(foreign, other.GetSubscription("shared"));
            Assert.Same(renewed, client.GetSubscription("shared"));
            foreign.Subscribe();
            renewed.Subscribe();
        }

        /// <summary>Dispose of a Subscribed subscription whose token expires clears its refresh timer.</summary>
        [Fact]
        public async Task DisposedSubscriptionDropsRefreshTimer()
        {
            _server.OnSubscribe = (_, _) => new SubscribeResult { Expires = true, Ttl = 60 };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("expiring", new CentrifugeSubscriptionOptions
            {
                Token = "t",
                GetToken = _ => Task.FromResult("t"),
            });
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            Assert.NotNull(Field(sub, "_refreshTimer"));

            sub.Dispose();

            Assert.Null(Field(sub, "_refreshTimer"));
        }

        /// <summary>
        /// The Unsubscribed handler of a disposed subscription can dispose the client synchronously,
        /// since closing doesn't wait for the unsubscribe reply.
        /// </summary>
        [Fact]
        public async Task DisposedSubscriptionHandlerDisposesClient()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("closing");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            sub.Unsubscribed += (_, _) => client.Dispose();

            await Task.Run(sub.Dispose).WaitAsync(Wait);

            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>ReadyAsync timing out throws a temporary CentrifugeTimeoutException, like a reply timeout.</summary>
        [Fact]
        public async Task ReadyTimeoutIsTemporaryTimeoutException()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("slow");
            sub.Subscribe();
            await held.Task.WaitAsync(Wait);

            var ex = await Assert.ThrowsAsync<CentrifugeTimeoutException>(() => sub.ReadyAsync(TimeSpan.FromMilliseconds(50)));

            Assert.True(ex.Temporary);
        }

        [Fact]
        public async Task SupersededGetTokenFailureHandsOverWithoutBackoff()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var options = WithoutBackoff();
            options.GetToken = _ =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult("t");
                firstCall.TrySetResult();
                return releaseFirst.Task;
            };
            var sub = client.NewSubscription("get-token", options);
            sub.Error += (_, e) => Record("error:" + e.Type);
            sub.Subscribe();
            await firstCall.Task.WaitAsync(Wait);

            sub.Unsubscribe();
            sub.Subscribe();
            releaseFirst.TrySetException(new InvalidOperationException("token service down"));

            await sub.ReadyAsync(Wait);
            Assert.DoesNotContain(Recorded(), r => r.StartsWith("error:subscribe", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SupersededGetStateFailureHandsOverWithoutError()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<CentrifugeStreamPosition>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var options = WithoutBackoff();
            options.GetState = _ =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(new CentrifugeStreamPosition(200, "second"));
                firstCall.TrySetResult();
                return releaseFirst.Task;
            };
            var sub = client.NewSubscription("get-state-failure", options);
            sub.Error += (_, e) => Record("error:" + e.Type);
            sub.Subscribe();
            await firstCall.Task.WaitAsync(Wait);

            sub.Unsubscribe();
            sub.Subscribe();
            releaseFirst.TrySetException(new InvalidOperationException("state service down"));

            await sub.ReadyAsync(Wait);
            Assert.DoesNotContain("error:getState", Recorded());
        }

        /// <summary>
        /// A connection token fetched for a connect attempt that has since been replaced must
        /// not be used on the new transport, and an "unauthorized" from it must not disconnect
        /// the client. The connect of the new attempt gets no reply yet, so the client stays
        /// Connecting. The stale GetToken is completed inline: its result has been handled when
        /// that returns. A send after the new session is up is the barrier for a connect it
        /// might have queued on the new transport.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StaleConnectTokenIsNotUsedByNewTransport(bool unauthorized)
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var releaseFirst = new TaskCompletionSource<string>();
            var calls = 0;
            var client = NewClient(new CentrifugeClientOptions
            {
                GetToken = () => Interlocked.Increment(ref calls) == 1 ? releaseFirst.Task : Task.FromResult("t"),
            });
            client.Connect();
            await UntilAwaited(releaseFirst.Task);

            client.Disconnect();
            client.Connect();
            var cmd = await held.Task.WaitAsync(Wait);
            await CompleteInline(() =>
            {
                if (unauthorized) releaseFirst.TrySetException(new CentrifugeUnauthorizedException());
                else releaseFirst.TrySetResult("stale");
            });

            Assert.Equal(CentrifugeClientState.Connecting, client.State);
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Connect = _server.ConnectResult });
            await client.ReadyAsync(Wait);
            await client.SendAsync(new byte[] { 1 });
            await Until(() => _server.Received.Any(c => c.Send != null));
            Assert.Equal(1, _server.Received.Count(c => c.Connect != null));
        }

        /// <summary>
        /// A permanent error answering the token refresh of an earlier subscribe session must not
        /// unsubscribe the current session. An RPC sent after the error reply is the barrier: its
        /// reply is processed after the error.
        /// </summary>
        [Fact]
        public async Task StaleSubRefreshErrorDoesNotUnsubscribeNewSession()
        {
            var refresh = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.SubRefresh != null && refresh.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;

            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var subscribed = System.Threading.Channels.Channel.CreateUnbounded<bool>();
            var unsubscribed = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeUnsubscribedEventArgs>();
            var sub = client.NewSubscription("refresh", new CentrifugeSubscriptionOptions
            {
                Token = "t",
                GetToken = _ => Task.FromResult("t"),
            });
            sub.Subscribed += (_, _) => subscribed.Writer.TryWrite(true);
            sub.Unsubscribed += (_, e) => unsubscribed.Writer.TryWrite(e);
            sub.Error += (_, e) => Record("error:" + e.Type);
            sub.Subscribe();
            await subscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            sub.RefreshToken();
            var cmd = await refresh.Task.WaitAsync(Wait);
            sub.Unsubscribe();
            sub.Subscribe();
            await subscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            await unsubscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 103, Message = "permission denied" } });
            await client.RpcAsync("sync", default).WaitAsync(Wait);

            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
            Assert.False(unsubscribed.Reader.TryRead(out var extra), $"stale refresh error unsubscribed: {extra?.Code}");
            Assert.DoesNotContain("error:refresh", Recorded());
        }

        /// <summary>
        /// An empty token (unauthorized) returned for the refresh of a session that was torn down
        /// doesn't disconnect the new session. The RPC that follows the release is the barrier.
        /// </summary>
        [Fact]
        public async Task StaleConnectionRefreshTokenDoesNotDisconnectNewSession()
        {
            var releaseFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var connected = System.Threading.Channels.Channel.CreateUnbounded<bool>();
            var disconnected = 0;
            var client = NewClient(new CentrifugeClientOptions
            {
                Token = "t",
                GetToken = () => Interlocked.Increment(ref calls) == 1 ? releaseFirst.Task : Task.FromResult("t"),
            });
            client.Connected += (_, _) => connected.Writer.TryWrite(true);
            client.Disconnected += (_, _) => Interlocked.Increment(ref disconnected);
            client.Connect();
            await connected.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            client.RefreshConnectionToken();

            _server.CloseConnection();
            await connected.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            releaseFirst.TrySetResult("");
            await client.RpcAsync("sync", default).WaitAsync(Wait);

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref disconnected));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>
        /// Nothing the transport still receives after Disconnect() is dispatched: a connect reply
        /// then must not bring the client back to Connected. The Disconnected handler makes the
        /// server answer the held connect and add a message. The test waits for the client's
        /// handler to take that frame (briefly: the closed transport may never deliver it), then
        /// for the event loop.
        /// </summary>
        [Fact]
        public async Task ConnectReplyReceivedAfterDisconnectIsNotApplied()
        {
            var connect = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && connect.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;

            var client = NewClient();
            var connectedEvents = 0;
            var messages = 0;
            client.Connected += (_, _) => Interlocked.Increment(ref connectedEvents);
            client.Message += (_, _) => Interlocked.Increment(ref messages);
            client.Connect();
            var cmd = await connect.Task.WaitAsync(Wait);

            var frameProcessed = FrameProcessed(client);
            client.Disconnected += (_, _) =>
            {
                // The transport is already closed when this runs: anything it still reads is stale.
                _ = _server.SendRepliesAsync(
                    new Reply { Id = cmd.Id, Connect = _server.ConnectResult },
                    new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("after") } } });
            };
            client.Disconnect();
            await Task.WhenAny(frameProcessed, Task.Delay(500));
            await client.LoopBarrierAsync().WaitAsync(Wait);

            Assert.Equal(0, Volatile.Read(ref connectedEvents));
            Assert.Equal(0, Volatile.Read(ref messages));
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        /// <summary>
        /// When Disconnect() + Connect() replaces a connect attempt, the old attempt's failure
        /// must not tear down the new transport. The old attempt waits in its GetToken and is
        /// failed once the new transport is connected; it is completed inline, so the failure
        /// has been handled when that returns. A send is the barrier for a reconnect it may
        /// have started, and it fails if the new transport was dropped.
        /// </summary>
        [Fact]
        public async Task StaleConnectFailureDoesNotTearDownNewTransport()
        {
            var releaseFirst = new TaskCompletionSource<string>();
            var calls = 0;
            var client = NewClient(new CentrifugeClientOptions
            {
                GetToken = () => Interlocked.Increment(ref calls) == 1 ? releaseFirst.Task : Task.FromResult("t"),
            });
            var connecting = System.Threading.Channels.Channel.CreateUnbounded<CentrifugeConnectingEventArgs>();
            client.Connect();
            await UntilAwaited(releaseFirst.Task);

            client.Connecting += (_, e) => connecting.Writer.TryWrite(e);
            client.Disconnect();
            client.Connect();
            await client.ReadyAsync(Wait);
            await CompleteInline(() => releaseFirst.TrySetException(
                new CentrifugeException(CentrifugeErrorCodes.ConnectionClosed, "connection closed", false)));
            await client.SendAsync(new byte[] { 1 });
            await Until(() => _server.Received.Any(c => c.Send != null));

            Assert.Equal(1, _server.Received.Count(c => c.Connect != null));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(CentrifugeConnectingCodes.ConnectCalled, (await connecting.Reader.ReadAsync().AsTask()).Code);
            Assert.False(connecting.Reader.TryRead(out var extra), $"unexpected reconnect: {extra?.Reason}");
        }

        /// <summary>
        /// During a teardown the client is already Connecting while the old transport may
        /// still be open. A subscribe started then (here by a server unsubscribe asking to
        /// resubscribe) must not be sent and applied on that transport, or the subscription
        /// would be Subscribed with nothing behind it on the server after the reconnect. The
        /// Connecting handler makes the server send that push, and it must not reach the
        /// subscription.
        /// </summary>
        [Fact]
        public async Task SubscribeDuringTeardownIsNotAppliedOnDyingTransport()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("teardown-window", new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(10),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
            });
            var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Subscribed += (_, _) => subscribed.TrySetResult();
            sub.Subscribe();
            await subscribed.Task.WaitAsync(Wait);
            sub.StateChanged += (_, e) => Record("state:" + e.NewState);
            sub.Subscribing += (_, _) => Record("subscribing");

            var pushProcessed = FrameProcessed(client);
            var gated = 0;
            client.Connecting += (_, _) =>
            {
                if (Interlocked.Exchange(ref gated, 1) != 0) return;
                // The push arrives on the transport the teardown closed: it is stale.
                _ = _server.SendPushAsync(new Push { Channel = "teardown-window", Unsubscribe = new Unsubscribe { Code = 2500, Reason = "resubscribe" } });
            };
            NoPing.Fire(client);
            await Task.WhenAny(pushProcessed, Task.Delay(500));
            await client.LoopBarrierAsync().WaitAsync(Wait);

            // Just the teardown's own events: the push didn't change the subscription again.
            Assert.Equal(new[] { "state:Subscribing", "subscribing" }, Recorded().Take(2));
            await client.ReadyAsync(Wait);
            await Until(() =>
            {
                var received = _server.Received.ToList();
                var lastConnect = received.FindLastIndex(c => c.Connect != null);
                return received.Skip(lastConnect + 1).Any(c => c.Subscribe != null);
            });
            Assert.Equal(2, _server.Received.Count(c => c.Connect != null));
        }

        /// <summary>
        /// A server unsubscribe with a code below 2500 ends the subscription on the server side, so
        /// the client sends no unsubscribe back. A send made after Unsubscribed arrives after any
        /// command the transition queued, which bounds the check.
        /// </summary>
        [Fact]
        public async Task ServerUnsubscribeSendsNoUnsubscribeCommand()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("server-unsubscribe");
            var unsubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Unsubscribed += (_, _) => unsubscribed.TrySetResult(true);
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            await _server.SendPushAsync(new Push { Channel = "server-unsubscribe", Unsubscribe = new Unsubscribe { Code = 2000, Reason = "server" } });
            await unsubscribed.Task.WaitAsync(Wait);
            await client.SendAsync(new byte[] { 1 });
            await Until(() => _server.Received.Any(c => c.Send != null));

            Assert.DoesNotContain(_server.Received, c => c.Unsubscribe != null);
        }

        /// <summary>
        /// A synchronous Dispose() from a handler running inside an event loop step must not
        /// deadlock waiting for that step.
        /// </summary>
        [Theory]
        [InlineData("publication")]
        [InlineData("subscribed")]
        [InlineData("transport-error")]
        [InlineData("connecting")]
        public async Task SynchronousDisposeFromHandlerDoesNotDeadlock(string handler)
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var once = 0;
            void DisposeHere()
            {
                if (Interlocked.Exchange(ref once, 1) != 0) return;
                client.Dispose();
                disposed.TrySetResult();
            }

            var sub = client.NewSubscription("dispose");
            if (handler == "subscribed")
            {
                sub.Subscribed += (_, _) => DisposeHere();
                sub.Subscribe();
            }
            else
            {
                sub.Subscribe();
                await sub.ReadyAsync(Wait);
                switch (handler)
                {
                    case "publication":
                        sub.Publication += (_, _) => DisposeHere();
                        await _server.PublishChannelAsync("dispose", Encoding.UTF8.GetBytes("x"));
                        break;
                    case "transport-error":
                        client.Error += (_, e) => { if (e.Type == "transport") DisposeHere(); };
                        _server.CloseConnection();
                        break;
                    case "connecting":
                        client.Connecting += (_, _) => DisposeHere();
                        _server.CloseConnection();
                        break;
                }
            }

            await disposed.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeClientState.Disconnected, client.State);
        }

        [Fact]
        public async Task ThrowingConnectedHandlerDoesNotStrandSubscriptions()
        {
            var client = NewClient();
            var sub = client.NewSubscription("after-throw");
            sub.Subscribe();
            var errors = 0;
            client.Error += (_, _) => Interlocked.Increment(ref errors);
            client.Connected += (_, _) => throw new InvalidOperationException("handler bug");
            client.Connect();

            await sub.ReadyAsync(Wait);
            Assert.True(Volatile.Read(ref errors) > 0);
        }

        /// <summary>
        /// A Connected handler that throws doesn't stop the server-side subscriptions of the
        /// connect reply from being applied: the next connect recovers them from the reply's
        /// positions and stops asking for a channel the reply left out.
        /// </summary>
        [Fact]
        public async Task ThrowingConnectedHandlerStillAppliesServerSubscriptions()
        {
            var first = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            first.Subs.Add("gone", new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e", Offset = 1 });
            var second = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            second.Subs.Add("kept", new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e", Offset = 5 });
            _server.ConnectResult = first;

            var client = NewClient();
            var connected = 0;
            client.Connected += (_, _) =>
            {
                if (Interlocked.Increment(ref connected) == 2) throw new InvalidOperationException("handler bug");
            };
            client.Connect();
            await client.ReadyAsync(Wait);

            _server.ConnectResult = second;
            _server.CloseConnection();
            await Until(() => Volatile.Read(ref connected) == 2);
            _server.ConnectResult = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            _server.CloseConnection();
            await Until(() => _server.Received.Count(c => c.Connect != null) == 3);

            var subs = _server.Received.Last(c => c.Connect != null).Connect.Subs;
            Assert.Equal(new[] { "kept" }, subs.Keys.ToArray());
            Assert.Equal(5UL, subs["kept"].Offset);
        }

        /// <summary>
        /// A reply sharing a frame with an earlier reply whose handlers outlast the command
        /// timeout must not time out: the whole frame is one event loop step, and a timer that
        /// fires meanwhile only runs after it. The Publication handler of the first reply keeps
        /// the loop busy (<see cref="TimeoutHold"/>) past the second command's timer.
        /// </summary>
        [Fact]
        public async Task ReplyBehindSlowHandlerInSameFrameDoesNotTimeOut()
        {
            var held = new List<Command>();
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
            {
                if (cmd.Subscribe == null) return null;
                lock (held)
                {
                    if (held.Count == 2) return null;
                    held.Add(cmd);
                    if (held.Count == 2) both.TrySetResult();
                }
                return FakeCentrifugoServer.NoReply;
            };
            var client = NewClient(new CentrifugeClientOptions { Timeout = CommandTimeout });
            client.Connect();
            await client.ReadyAsync(Wait);

            var connecting = 0;
            var errors = 0;
            client.Connecting += (_, _) => Interlocked.Increment(ref connecting);
            var a = client.NewSubscription("a");
            var b = client.NewSubscription("b");
            var hold = new TimeoutHold();
            a.Publication += (_, _) => hold.Hold();
            b.Error += (_, _) => Interlocked.Increment(ref errors);
            a.Subscribe();
            b.Subscribe();
            await both.Task.WaitAsync(Wait);
            hold.Armed();

            Command ca, cb;
            lock (held)
            {
                ca = held.Single(c => c.Subscribe.Channel == "a");
                cb = held.Single(c => c.Subscribe.Channel == "b");
            }
            try
            {
                await _server.SendRepliesAsync(
                    new Reply
                    {
                        Id = ca.Id,
                        Subscribe = new SubscribeResult
                        {
                            Recoverable = true, Positioned = true, Epoch = "e", Offset = 1, Recovered = true,
                            Publications = { Pub(1, "r1") },
                        },
                    },
                    new Reply { Id = cb.Id, Subscribe = new SubscribeResult() });
                await hold.PastTimeoutAsync();
            }
            finally
            {
                hold.Release();
            }

            await b.ReadyAsync(Wait);
            Assert.Equal(0, Volatile.Read(ref errors));
            Assert.Equal(0, Volatile.Read(ref connecting));
        }

        /// <summary>
        /// A Subscribed handler slower than the command timeout must not make the reply being
        /// applied time out and force a reconnect: the call is removed and its timer cancelled
        /// before the reply is applied. The handler keeps the loop busy
        /// (<see cref="TimeoutHold"/>) past the command's timer.
        /// </summary>
        [Fact]
        public async Task SlowSubscribedHandlerDoesNotTimeOutAppliedReply()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient(new CentrifugeClientOptions { Timeout = CommandTimeout });
            client.Connect();
            await client.ReadyAsync(Wait);

            var errors = 0;
            var connecting = 0;
            var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, _) => Interlocked.Increment(ref connecting);
            var sub = client.NewSubscription("slow-subscribed");
            var hold = new TimeoutHold();
            sub.Error += (_, _) => Interlocked.Increment(ref errors);
            sub.Subscribed += (_, _) =>
            {
                hold.Hold();
                subscribed.TrySetResult();
            };
            sub.Subscribe();
            var cmd = await held.Task.WaitAsync(Wait);
            hold.Armed();

            try
            {
                await _server.SendReplyAsync(new Reply { Id = cmd.Id, Subscribe = new SubscribeResult() });
                await hold.PastTimeoutAsync();
            }
            finally
            {
                hold.Release();
            }

            await subscribed.Task.WaitAsync(Wait);
            Assert.Equal(0, Volatile.Read(ref errors));
            Assert.Equal(0, Volatile.Read(ref connecting));
        }

        /// <summary>
        /// Delivery of recovered publications stops as soon as a handler unsubscribes. The
        /// message at the end of the frame is the barrier: it is dispatched after the whole reply.
        /// </summary>
        [Fact]
        public async Task RecoveredDeliveryStopsWhenHandlerUnsubscribes()
        {
            _server.OnCommandFrame = cmd => cmd.Subscribe == null ? null : new[]
            {
                new Reply
                {
                    Id = cmd.Id,
                    Subscribe = new SubscribeResult
                    {
                        Recoverable = true, Positioned = true, Epoch = "e", Offset = 2, Recovered = true,
                        Publications = { Pub(1, "r1"), Pub(2, "r2") },
                    },
                },
                new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("barrier") } } },
            };
            var client = NewClient();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Message += (_, _) => barrier.TrySetResult();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("recover-stop");
            sub.Publication += (_, e) =>
            {
                Record("pub" + e.Offset);
                if (e.Offset == 1) sub.Unsubscribe();
            };
            sub.Subscribe();

            await barrier.Task.WaitAsync(Wait);
            Assert.Equal(new[] { "pub1" }, Recorded());
        }

        /// <summary>
        /// When a handler cuts a recovery short (here by disconnecting at the first recovered
        /// publication), the position stays at the last publication delivered, and the next
        /// recovery delivers the remainder instead of skipping it.
        /// </summary>
        [Fact]
        public async Task RecoveryCutShortByHandlerResumesFromLastDelivered()
        {
            _server.OnSubscribe = (_, req) => req.Recover
                ? new SubscribeResult
                {
                    Recoverable = true, Positioned = true, Epoch = "e", Offset = 3, Recovered = true,
                    Publications = { Pub(1, "r1"), Pub(2, "r2"), Pub(3, "r3") },
                }
                : new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e" };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("recover-resume");
            var cut = 0;
            sub.Publication += (_, e) =>
            {
                if (e.Offset == 1 && Interlocked.Increment(ref cut) == 1) client.Disconnect();
            };
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            _server.CloseConnection();
            await Until(() => client.State == CentrifugeClientState.Disconnected);
            client.Connect();
            await Until(() => _server.Received.Count(c => c.Subscribe != null) == 3);

            var resumed = _server.LastSubscribe()!;
            Assert.True(resumed.Recover);
            Assert.Equal(1UL, resumed.Offset);
        }

        /// <summary>
        /// The same for a server-side subscription: the next connect recovers from the last
        /// publication delivered before the handler disconnected.
        /// </summary>
        [Fact]
        public async Task ServerSideRecoveryCutShortByHandlerResumesFromLastDelivered()
        {
            var connects = 0;
            _server.OnCommand = cmd =>
            {
                if (cmd.Connect == null) return null;
                var result = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
                result.Subs.Add("ss", Interlocked.Increment(ref connects) == 2
                    ? new SubscribeResult
                    {
                        Recoverable = true, Positioned = true, Epoch = "e", Offset = 3, Recovered = true,
                        Publications = { Pub(1, "r1"), Pub(2, "r2"), Pub(3, "r3") },
                    }
                    : new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e" });
                return new Reply { Id = cmd.Id, Connect = result };
            };
            var client = NewClient();
            var cut = 0;
            client.Publication += (_, e) =>
            {
                if (e.Offset == 1 && Interlocked.Increment(ref cut) == 1) client.Disconnect();
            };
            client.Connect();
            await client.ReadyAsync(Wait);

            _server.CloseConnection();
            await Until(() => client.State == CentrifugeClientState.Disconnected);
            client.Connect();
            await Until(() => _server.Received.Count(c => c.Connect != null) == 3);

            Assert.Equal(1UL, _server.Received.Last(c => c.Connect != null).Connect.Subs["ss"].Offset);
        }

        /// <summary>
        /// A reply received before the call's timeout step ran is delivered, not the timeout: a handler
        /// opening the frame holds the loop past the call's timer, and the reply behind it in the frame is
        /// processed before the timeout (as centrifuge-js lets data already received go first).
        /// </summary>
        [Fact]
        public async Task ReplyReceivedBeforeTimeoutStepIsDelivered()
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd => cmd.Rpc != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient(new CentrifugeClientOptions { Timeout = CommandTimeout });
            client.Connect();
            await client.ReadyAsync(Wait);

            var hold = new TimeoutHold();
            client.Message += (_, _) => hold.Hold();
            var rpc = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            var cmd = await held.Task.WaitAsync(Wait);
            hold.Armed();

            try
            {
                await _server.SendRepliesAsync(
                    new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("m") } } },
                    new Reply { Id = cmd.Id, Rpc = new RPCResult() });
                await hold.PastTimeoutAsync();
            }
            finally
            {
                hold.Release();
            }
            await rpc.WaitAsync(Wait);
        }

        /// <summary>
        /// A failed recovery only moves the position to the new top after Subscribed reported the
        /// gap: if a handler unsubscribed before that, the next subscribe recovers from the old
        /// position. The first subscribe is awaited with its Subscribed event rather than
        /// ReadyAsync (which resolves before the reply's events), so the handler added afterwards
        /// only sees the resubscribe.
        /// </summary>
        [Fact]
        public async Task FailedRecoveryGapIsNotLostWhenSubscribedIsSkipped()
        {
            _server.OnSubscribe = (_, req) => new SubscribeResult
            {
                Recoverable = true, Positioned = true, Epoch = "e", Offset = req.Recover ? 50ul : 10ul,
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("gap");
            var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sub.Subscribed += (_, _) => subscribed.TrySetResult();
            sub.Subscribe();
            await subscribed.Task.WaitAsync(Wait);

            var skip = 1;
            sub.StateChanged += (_, e) =>
            {
                if (e.NewState == CentrifugeSubscriptionState.Subscribed && Interlocked.Exchange(ref skip, 0) == 1)
                    sub.Unsubscribe();
            };
            _server.CloseConnection();
            await Until(() => Volatile.Read(ref skip) == 0 && sub.State == CentrifugeSubscriptionState.Unsubscribed);

            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            Assert.Equal(10ul, _server.LastSubscribe()!.Offset);
        }

        /// <summary>
        /// A push routed by channel name doesn't reach a subscription that a handler unsubscribed
        /// earlier in the same frame: the position stays at the last recovered publication
        /// delivered, and the next subscribe recovers the rest.
        /// </summary>
        [Fact]
        public async Task PushAfterUnsubscribeInFrameDoesNotSkipRecoveredPublications()
        {
            var subscribes = 0;
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Subscribe != null && Interlocked.Increment(ref subscribes) == 2 && held.TrySetResult(cmd)
                    ? FakeCentrifugoServer.NoReply
                    : null;
            _server.OnSubscribe = (_, _) => new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e" };
            var client = NewClient();
            var frameDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Message += (_, _) => frameDone.TrySetResult();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("cut");
            sub.Publication += (_, e) =>
            {
                if (e.Offset == 1) sub.Unsubscribe();
            };
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            _server.CloseConnection();
            var cmd = await held.Task.WaitAsync(Wait);
            await _server.SendRepliesAsync(
                new Reply
                {
                    Id = cmd.Id,
                    Subscribe = new SubscribeResult
                    {
                        Recoverable = true, Positioned = true, Epoch = "e", Offset = 2, Recovered = true,
                        Publications = { Pub(1, "p1"), Pub(2, "p2") },
                    },
                },
                new Reply { Push = new Push { Channel = "cut", Pub = Pub(3, "p3") } },
                new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("end") } } });
            await frameDone.Task.WaitAsync(Wait);

            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            Assert.Equal(1ul, _server.LastSubscribe()!.Offset);
        }

        /// <summary>
        /// A teardown transition handler that throws doesn't stop the reconnect: the session is
        /// restored and the subscription subscribes again.
        /// </summary>
        [Fact]
        public async Task ThrowingSubscribingHandlerDoesNotStopReconnect()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("reconnect");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);
            sub.Subscribing += (_, _) => throw new InvalidOperationException("subscribing handler");

            _server.CloseConnection();
            await Until(() => _server.Received.Count(c => c.Connect != null) == 2);
            await sub.ReadyAsync(Wait);
        }

        /// <summary>
        /// An Error handler that throws doesn't break the SDK code reporting the error: with both
        /// Connected and Error throwing, the session is still established and usable.
        /// </summary>
        [Fact]
        public async Task ThrowingErrorHandlerDoesNotBreakEstablishedSession()
        {
            _server.OnCommand = cmd => cmd.Rpc != null ? new Reply { Id = cmd.Id, Rpc = new RPCResult() } : null;
            var client = NewClient();
            client.Connected += (_, _) => throw new InvalidOperationException("connected handler");
            client.Error += (_, _) => throw new InvalidOperationException("error handler");
            client.Connect();
            await client.ReadyAsync(Wait);

            await client.RpcAsync("method", ReadOnlyMemory<byte>.Empty).WaitAsync(Wait);
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>
        /// A subscribe that fails because its connection was torn down belongs to an attempt that
        /// is no longer current: it raises no Error, the subscription just retries on the next session.
        /// </summary>
        [Fact]
        public async Task SubscribeFailedByTeardownRaisesNoError()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("torn-down");
            sub.Error += (_, e) => Record("error:" + e.Type);
            sub.Subscribe();
            await held.Task.WaitAsync(Wait);

            _server.CloseConnection();
            await sub.ReadyAsync(Wait);
            Assert.DoesNotContain(Recorded(), r => r.StartsWith("error:subscribe", StringComparison.Ordinal));
        }

        /// <summary>An error reply to an unsubscribe command makes its session reconnect (as centrifuge-js).</summary>
        [Fact]
        public async Task UnsubscribeErrorReplyReconnectsSession()
        {
            _server.OnCommand = cmd => cmd.Unsubscribe != null
                ? new Reply { Id = cmd.Id, Error = new Error { Code = 100, Message = "internal" } }
                : null;
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var sub = client.NewSubscription("unsub-error");
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            sub.Unsubscribe();

            await Until(() => _server.Received.Count(c => c.Connect != null) == 2);
        }

        /// <summary>A connect reply with no result fails the connect, and the connect is retried.</summary>
        [Fact]
        public async Task ConnectReplyWithoutResultIsRetried()
        {
            var connects = 0;
            _server.OnCommand = cmd =>
                cmd.Connect != null && Interlocked.Increment(ref connects) == 1 ? new Reply { Id = cmd.Id } : null;
            var client = NewClient();
            Exception? error = null;
            client.Error += (_, e) => error ??= e.Exception;
            client.Connect();

            await client.ReadyAsync(Wait);
            Assert.Equal(2, connects);
            Assert.IsType<System.IO.InvalidDataException>(error);
        }

        /// <summary>A subscribe reply with no result fails the attempt, and the subscribe is retried.</summary>
        [Fact]
        public async Task SubscribeReplyWithoutResultIsRetried()
        {
            var subscribes = 0;
            _server.OnCommand = cmd =>
                cmd.Subscribe != null && Interlocked.Increment(ref subscribes) == 1 ? new Reply { Id = cmd.Id } : null;
            var client = NewClient();
            client.Connect();
            var sub = client.NewSubscription("no-result", new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(1),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
            });
            Exception? error = null;
            sub.Error += (_, e) => error ??= e.Exception;
            sub.Subscribe();

            await sub.ReadyAsync(Wait);
            Assert.Equal(2, subscribes);
            Assert.IsType<System.IO.InvalidDataException>(error);
        }

        /// <summary>
        /// A server-side channel missing from the connect reply stays registered until
        /// ServerUnsubscribed is raised for it, so a handler disconnecting before then doesn't lose it.
        /// </summary>
        [Fact]
        public async Task ServerUnsubscribedIsNotLostWhenHandlerDisconnects()
        {
            var connects = 0;
            _server.OnCommand = cmd =>
            {
                if (cmd.Connect == null) return null;
                var result = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
                result.Subs.Add("a", new SubscribeResult());
                if (Interlocked.Increment(ref connects) == 1) result.Subs.Add("b", new SubscribeResult());
                return new Reply { Id = cmd.Id, Connect = result };
            };
            var client = NewClient();
            var cut = 0;
            client.ServerSubscribed += (_, e) =>
            {
                if (Volatile.Read(ref connects) == 2 && Interlocked.Increment(ref cut) == 1) client.Disconnect();
            };
            client.ServerUnsubscribed += (_, e) => Record("unsubscribed:" + e.Channel);
            client.Connect();
            await client.ReadyAsync(Wait);

            _server.CloseConnection();
            await Until(() => client.State == CentrifugeClientState.Disconnected);
            client.Connect();

            await Until(() => Recorded().Contains("unsubscribed:b"));
        }

        /// <summary>
        /// A teardown fails the pending calls of its session right away: a transition handler that
        /// throws can't leave them unresolved.
        /// </summary>
        [Fact]
        public async Task PendingCallFailsWhenTeardownHandlerThrows()
        {
            _server.OnCommand = cmd => cmd.Rpc != null ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            client.Disconnected += (_, _) => throw new InvalidOperationException("handler");

            var rpc = client.RpcAsync("method", ReadOnlyMemory<byte>.Empty);
            await Until(() => _server.Received.Any(c => c.Rpc != null));
            client.Disconnect();

            var ex = await Assert.ThrowsAsync<CentrifugeException>(() => rpc.WaitAsync(Wait));
            Assert.Equal(CentrifugeErrorCodes.ConnectionClosed, ex.Code);
        }

        /// <summary>
        /// A message that fails to parse is reported at its position in the frame: an Error
        /// handler that disconnects keeps the messages that came before it.
        /// </summary>
        [Fact]
        public async Task ParseErrorIsReportedAfterPrecedingMessages()
        {
            var client = NewClient();
            client.Message += (_, _) => Record("message");
            client.Error += (_, e) =>
            {
                if (e.Type != "parse") return;
                Record("parse");
                client.Disconnect();
            };
            client.Connect();
            await client.ReadyAsync(Wait);

            using var frame = new System.IO.MemoryStream();
            new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("m") } } }.WriteDelimitedTo(frame);
            frame.Write(new byte[] { 0x02, 0xFF, 0xFF }, 0, 3);
            await _server.SendRawAsync(frame.ToArray());

            await Until(() => Recorded().Contains("parse"));
            Assert.Equal(new[] { "message", "parse" }, Recorded());
        }

        /// <summary>
        /// A message that fails to parse makes the session reconnect: the messages after it aren't
        /// dispatched, so recovery resumes from before the lost one.
        /// </summary>
        [Fact]
        public async Task ParseErrorReconnectsWithoutDispatchingRest()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var messages = 0;
            client.Message += (_, _) => Interlocked.Increment(ref messages);
            var connecting = new TaskCompletionSource<CentrifugeConnectingEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, e) => connecting.TrySetResult(e);

            using var frame = new System.IO.MemoryStream();
            frame.Write(new byte[] { 0x02, 0xFF, 0xFF }, 0, 3);
            new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("m") } } }.WriteDelimitedTo(frame);
            await _server.SendRawAsync(frame.ToArray());

            var args = await connecting.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeConnectingCodes.TransportClosed, args.Code);
            Assert.Equal(0, Volatile.Read(ref messages));
        }

        /// <summary>Messages that precede a malformed one in a frame are still dispatched.</summary>
        [Fact]
        public async Task ReplyBeforeMalformedTailOfFrameIsApplied()
        {
            var connect = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && connect.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient();
            client.Connected += (_, e) => Record("connected:" + e.ClientId);
            client.Connect();
            var cmd = await connect.Task.WaitAsync(Wait);

            using var frame = new System.IO.MemoryStream();
            new Reply { Id = cmd.Id, Connect = new ConnectResult { Client = "first", Version = "0.0.0" } }.WriteDelimitedTo(frame);
            frame.Write(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }, 0, 6);
            await _server.SendRawAsync(frame.ToArray());

            await Until(() => Recorded().Length > 0);
            Assert.Equal("connected:first", Recorded()[0]);
        }

        /// <summary>
        /// Once an event handler moves the subscription on (here by unsubscribing at the
        /// Subscribed transition), the remaining events of the reply are not raised. The
        /// message at the end of the frame is the barrier.
        /// </summary>
        [Fact]
        public async Task SubscribedNotRaisedAfterStateChangedHandlerUnsubscribes()
        {
            _server.OnCommandFrame = cmd => cmd.Subscribe == null ? null : new[]
            {
                new Reply { Id = cmd.Id, Subscribe = new SubscribeResult() },
                new Reply { Push = new Push { Message = new Message { Data = ByteString.CopyFromUtf8("barrier") } } },
            };
            var client = NewClient();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Message += (_, _) => barrier.TrySetResult();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("unsubscribe-in-handler");
            sub.StateChanged += (_, e) =>
            {
                Record("state:" + e.NewState);
                if (e.NewState == CentrifugeSubscriptionState.Subscribed) sub.Unsubscribe();
            };
            sub.Subscribed += (_, _) => Record("subscribed");
            sub.Unsubscribed += (_, _) => Record("unsubscribed");
            sub.Subscribe();

            await barrier.Task.WaitAsync(Wait);
            Assert.Equal(new[] { "state:Subscribing", "state:Subscribed", "state:Unsubscribed", "unsubscribed" }, Recorded());
        }

        /// <summary>
        /// If a handler skipped the Subscribed of the first subscribe (by unsubscribing at the
        /// transition), no stream position is kept: the next subscribe starts fresh rather than
        /// recovering from a base the app never saw.
        /// </summary>
        [Fact]
        public async Task SkippedFirstSubscribedLeavesNoPositionToRecoverFrom()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null : new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e", Offset = 10 },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("skipped-subscribed", new CentrifugeSubscriptionOptions { Recoverable = true });
            var first = true;
            sub.StateChanged += (_, e) =>
            {
                if (e.NewState != CentrifugeSubscriptionState.Subscribed || !first) return;
                first = false;
                sub.Unsubscribe();
            };
            sub.Subscribe();
            await Until(() => sub.State == CentrifugeSubscriptionState.Unsubscribed);
            sub.Subscribe();
            await sub.ReadyAsync(Wait);

            var subscribes = _server.Received.Where(c => c.Subscribe != null).ToList();
            Assert.Equal(2, subscribes.Count);
            Assert.False(subscribes[1].Subscribe.Recover);
        }

        /// <summary>
        /// The base reported by Subscribed is stored before the handler runs, so a handler that
        /// reconnects right away resubscribes recovering from it.
        /// </summary>
        [Fact]
        public async Task ReconnectFromSubscribedHandlerRecoversFromReportedBase()
        {
            _server.OnCommand = cmd => cmd.Subscribe == null ? null : new Reply
            {
                Id = cmd.Id,
                Subscribe = new SubscribeResult { Recoverable = true, Positioned = true, Epoch = "e", Offset = 10 },
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("reconnect-in-subscribed", new CentrifugeSubscriptionOptions { Recoverable = true });
            var first = true;
            sub.Subscribed += (_, _) =>
            {
                if (!first) return;
                first = false;
                client.Disconnect();
                client.Connect();
            };
            sub.Subscribe();
            await Until(() => _server.Received.Count(c => c.Subscribe != null) == 2);

            var resubscribe = _server.Received.Last(c => c.Subscribe != null).Subscribe;
            Assert.True(resubscribe.Recover);
            Assert.Equal(10UL, resubscribe.Offset);
            Assert.Equal("e", resubscribe.Epoch);
        }

        /// <summary>
        /// A token already cancelled when the command would be queued fails the call, and nothing
        /// is sent. A send queued after both calls failed is the barrier: anything they queued
        /// arrives before it.
        /// </summary>
        [Fact]
        public async Task CancelledCommandIsNotSent()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new byte[] { 1 }, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RpcAsync("m", new byte[] { 1 }, cancelled.Token));
            await client.SendAsync(new byte[] { 2 });
            await Until(() => _server.Received.Any(c => c.Send != null));

            Assert.DoesNotContain(_server.Received, c => c.Rpc != null);
            Assert.Equal(new byte[] { 2 }, Assert.Single(_server.Received, c => c.Send != null).Send.Data.ToByteArray());
        }

        /// <summary>
        /// A failure of the subscription token provider, a timeout included, is a token error
        /// retried with backoff rather than a subscribe timeout, so the client doesn't reconnect.
        /// </summary>
        [Fact]
        public async Task SubscribeTokenFailureIsRetriedAsTokenError()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var calls = 0;
            var sub = client.NewSubscription("token-failure", new CentrifugeSubscriptionOptions
            {
                MinResubscribeDelay = TimeSpan.FromMilliseconds(1),
                MaxResubscribeDelay = TimeSpan.FromMilliseconds(50),
                GetToken = _ => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<string>(new CentrifugeTimeoutException())
                    : Task.FromResult("t"),
            });
            (int Code, string Type)? error = null;
            sub.Error += (_, e) => error ??= (e.Code, e.Type);
            sub.Subscribe();

            await sub.ReadyAsync(Wait);
            Assert.Equal((CentrifugeErrorCodes.SubscriptionSubscribeToken, "subscribeToken"), error);
            Assert.Equal(1, _server.Received.Count(c => c.Connect != null));
        }

        /// <summary>
        /// A failure of the connection token provider, even one carrying a permanent server code,
        /// is a token error retried with backoff, not the server rejecting the connect.
        /// </summary>
        [Fact]
        public async Task ConnectTokenFailureIsRetriedAsTokenError()
        {
            var calls = 0;
            var client = NewClient(new CentrifugeClientOptions
            {
                GetToken = () => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<string>(new CentrifugeException(103, "permission denied", false))
                    : Task.FromResult("t"),
            });
            (int Code, string Type)? error = null;
            client.Error += (_, e) => error ??= (e.Code, e.Type);
            client.Connect();

            await client.ReadyAsync(Wait);
            Assert.Equal((CentrifugeErrorCodes.ClientConnectToken, "connectToken"), error);
        }

        /// <summary>
        /// An expired static token (109) with no GetToken to replace it isn't retried: an Error of
        /// type configuration, then Disconnected with Unauthorized (as centrifuge-js). Setting a
        /// token afterwards replaces it, and the next connect sends the new one.
        /// </summary>
        [Fact]
        public async Task ExpiredStaticTokenWithoutGetTokenDisconnectsUnauthorized()
        {
            _server.OnCommand = cmd => cmd.Connect is { Token: "expired" }
                ? new Reply { Id = cmd.Id, Error = new Error { Code = 109, Message = "token expired" } }
                : null;
            var client = NewClient(new CentrifugeClientOptions { Token = "expired" });
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var disconnected = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Error += (_, e) => errors.Enqueue(e.Type);
            client.Disconnected += (_, e) => disconnected.TrySetResult(e);
            client.Connect();

            Assert.Equal(CentrifugeDisconnectedCodes.Unauthorized, (await disconnected.Task.WaitAsync(Wait)).Code);
            Assert.Contains("configuration", errors);

            client.SetToken("fresh");
            client.Connect();
            await client.ReadyAsync(Wait);
            Assert.Equal("fresh", _server.Received.Last(c => c.Connect != null).Connect.Token);
        }

        /// <summary>
        /// A 109 reply for a token the app replaced while the connect was pending doesn't mark the
        /// new token expired: the next connect uses it (as centrifuge-js).
        /// </summary>
        [Fact]
        public async Task ExpiredReplyForReplacedTokenKeepsNewToken()
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd => cmd.Connect is { Token: "old" } && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient(new CentrifugeClientOptions { Token = "old" });
            client.Connect();
            var cmd = await held.Task.WaitAsync(Wait);

            client.SetToken("fresh");
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 109, Message = "token expired" } });

            await client.ReadyAsync(Wait);
            Assert.Equal("fresh", _server.Received.Last(c => c.Connect != null).Connect.Token);
        }

        /// <summary>
        /// A connect error 109 followed by a Disconnect in the same frame still marks the token
        /// for refresh, in the frame's step and before the Disconnect ends the attempt: the next
        /// connect gets a fresh token.
        /// </summary>
        [Fact]
        public async Task ExpiredTokenBeforeDisconnectInFrameIsRefreshed()
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd => cmd.Connect != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient(new CentrifugeClientOptions
            {
                Token = "expired",
                GetToken = () => Task.FromResult("fresh"),
            });
            client.Connect();
            var cmd = await held.Task.WaitAsync(Wait);

            await _server.SendRepliesAsync(
                new Reply { Id = cmd.Id, Error = new Error { Code = 109, Message = "token expired" } },
                new Reply { Push = new Push { Disconnect = new Disconnect { Code = 3001, Reason = "shutdown" } } });

            await client.ReadyAsync(Wait);
            Assert.Equal("fresh", _server.Received.Last(c => c.Connect != null).Connect.Token);
        }

        /// <summary>Subscriptions created from one shared options object keep separate tokens: the SDK doesn't write to the options.</summary>
        [Fact]
        public async Task SharedSubscriptionOptionsKeepTokensApart()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var options = new CentrifugeSubscriptionOptions { GetToken = channel => Task.FromResult("token-" + channel) };

            var a = client.NewSubscription("a", options);
            a.Subscribe();
            await a.ReadyAsync(Wait);
            var b = client.NewSubscription("b", options);
            b.Subscribe();
            await b.ReadyAsync(Wait);

            Assert.Equal("token-b", _server.Received.Last(c => c.Subscribe?.Channel == "b").Subscribe.Token);
            Assert.Null(options.Token);
        }

        /// <summary>
        /// A permanent connect error disconnects with the server's code even if the server closes
        /// the connection right after it: the reply's step runs before the step of the close.
        /// </summary>
        [Fact]
        public async Task PermanentConnectErrorFollowedByCloseDisconnects()
        {
            var held = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd => cmd.Connect != null && held.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient();
            var disconnected = new TaskCompletionSource<CentrifugeDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, e) => disconnected.TrySetResult(e);
            client.Connect();
            var cmd = await held.Task.WaitAsync(Wait);

            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 103, Message = "permission denied" } });
            await _server.CloseConnectionGracefullyAsync();

            Assert.Equal(103, (await disconnected.Task.WaitAsync(Wait)).Code);
        }

        /// <summary>A transport closing without a server code reconnects with TransportClosed (as centrifuge-js).</summary>
        [Fact]
        public async Task TransportCloseReconnectsWithTransportClosedCode()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);
            var connecting = new TaskCompletionSource<CentrifugeConnectingEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Connecting += (_, e) => connecting.TrySetResult(e);

            _server.CloseConnection();

            var args = await connecting.Task.WaitAsync(Wait);
            Assert.Equal(CentrifugeConnectingCodes.TransportClosed, args.Code);
            Assert.Equal("transport closed", args.Reason);
        }

        /// <summary>
        /// As with a subscribe reply, once a handler disconnected, neither Connected nor the
        /// reply's server-side subscription events come after Disconnected. A new server-side
        /// channel whose ServerSubscribed the app never saw isn't kept: the disconnect raises no
        /// ServerSubscribing for it, and the next connect doesn't try to recover it. The events
        /// are checked once the client's handler took the frame (<see cref="FrameProcessed"/>).
        /// </summary>
        [Theory]
        [InlineData("state")]
        [InlineData("connected")]
        public async Task ConnectReplyEventsStopAfterHandlerDisconnects(string handler)
        {
            var result = new ConnectResult { Client = "c", Version = "0.0.0", Ping = 25 };
            result.Subs.Add("ss", new SubscribeResult
            {
                Recoverable = true, Positioned = true, Epoch = "e", Offset = 1, Recovered = true,
                Publications = { Pub(1, "r1") },
            });
            _server.ConnectResult = result;
            var connect = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.Connect != null && connect.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;

            var client = NewClient();
            client.StateChanged += (_, e) =>
            {
                if (handler == "state" && e.NewState == CentrifugeClientState.Connected) client.Disconnect();
            };
            client.Connected += (_, _) =>
            {
                Record("connected");
                if (handler == "connected") client.Disconnect();
            };
            client.Disconnected += (_, _) => Record("disconnected");
            client.ServerSubscribing += (_, e) => Record("server-subscribing:" + e.Channel);
            client.ServerSubscribed += (_, e) => Record("server-subscribed:" + e.Channel);
            client.Publication += (_, e) => Record("server-pub" + e.Offset);
            client.Connect();
            var cmd = await connect.Task.WaitAsync(Wait);

            var replyProcessed = FrameProcessed(client);
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Connect = result });
            await replyProcessed.WaitAsync(Wait);
            Assert.Equal(handler == "state"
                ? new[] { "disconnected" }
                : new[] { "connected", "disconnected" }, Recorded());

            handler = "none";
            client.Connect();
            await client.ReadyAsync(Wait);
            var reconnect = _server.Received.Last(c => c.Connect != null);
            Assert.False(reconnect.Connect.Subs.ContainsKey("ss"));
        }

        /// <summary>
        /// Subscribe and unsubscribe commands reach the server in the order of the transitions.
        /// When the subscription is toggled while the first subscribe and unsubscribe are still
        /// unanswered, the second unsubscribe must not wait for the first one's reply and so
        /// arrive after the latest subscribe, which would leave the server unsubscribed while
        /// the client is Subscribed.
        /// </summary>
        [Fact]
        public async Task SubscribeAndUnsubscribeReachServerInTransitionOrder()
        {
            var heldSubscribe = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldUnsubscribe = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
            {
                if (cmd.Subscribe != null && heldSubscribe.TrySetResult(cmd)) return FakeCentrifugoServer.NoReply;
                if (cmd.Unsubscribe != null && heldUnsubscribe.TrySetResult(cmd)) return FakeCentrifugoServer.NoReply;
                return null;
            };
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var sub = client.NewSubscription("toggle", WithoutBackoff());
            sub.Subscribe();
            var subscribe = await heldSubscribe.Task.WaitAsync(Wait);
            sub.Unsubscribe();
            var unsubscribe = await heldUnsubscribe.Task.WaitAsync(Wait);
            sub.Subscribe();
            sub.Unsubscribe();
            sub.Subscribe();

            await _server.SendReplyAsync(new Reply { Id = subscribe.Id, Subscribe = new SubscribeResult() });
            await sub.ReadyAsync(Wait);
            await _server.SendReplyAsync(new Reply { Id = unsubscribe.Id, Unsubscribe = new UnsubscribeResult() });
            await Until(() => _server.Received.Count(c => c.Unsubscribe != null) == 2);

            // Every transition sends its own command, in order (as centrifuge-js), so the server ends up subscribed.
            var wire = _server.Received
                .Where(c => c.Subscribe != null || c.Unsubscribe != null)
                .Select(c => c.Subscribe != null ? "sub" : "unsub");
            Assert.Equal(new[] { "sub", "unsub", "sub", "unsub", "sub" }, wire);
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
        }

        /// <summary>
        /// A reconnect scheduled by a failed connect attempt belongs to that attempt: once the
        /// Error handler restarted the client, it must not replace the new transport. The attempt
        /// waits in its GetToken and is failed with a temporary error, completed inline, so the
        /// Error handler and the reconnect decision have run when that returns, and no reconnect
        /// timer is set.
        /// </summary>
        [Fact]
        public async Task ReconnectOfSupersededConnectAttemptDoesNotReplaceNewTransport()
        {
            var releaseFirst = new TaskCompletionSource<string>();
            var calls = 0;
            var client = NewClient(new CentrifugeClientOptions
            {
                GetToken = () => Interlocked.Increment(ref calls) == 1 ? releaseFirst.Task : Task.FromResult("t"),
            });
            var restarted = 0;
            client.Error += (_, e) =>
            {
                if (e.Type != "connectToken" || Interlocked.Exchange(ref restarted, 1) != 0) return;
                client.Disconnect();
                client.Connect();
            };
            var sub = client.NewSubscription("restart");
            sub.Subscribe();
            client.Connect();
            await UntilAwaited(releaseFirst.Task);

            await CompleteInline(() => releaseFirst.TrySetException(new CentrifugeException(100, "internal", true)));
            await Until(() => Volatile.Read(ref restarted) == 1);
            await client.LoopBarrierAsync().WaitAsync(Wait);
            Assert.Null(Field(client, "_reconnectTimer"));

            await sub.ReadyAsync(Wait);
            Assert.Equal(1, _server.Received.Count(c => c.Connect != null));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>
        /// The no-ping timer of a session that has ended (its callback began before the reconnect)
        /// is tied to that session's transport and leaves the next session alone.
        /// </summary>
        [Fact]
        public async Task StaleNoPingDoesNotTearDownNextSession()
        {
            var client = NewClient();
            var connected = 0;
            client.Connected += (_, _) => Interlocked.Increment(ref connected);
            client.Connect();
            await client.ReadyAsync(Wait);
            var first = NoPing.Transport(client)!;

            _server.CloseConnection();
            await Until(() => Volatile.Read(ref connected) == 2);
            client.NoPing(first);

            Assert.Equal(CentrifugeClientState.Connected, client.State);
            Assert.Equal(2, _server.Received.Count(c => c.Connect != null));
        }

        /// <summary>
        /// A teardown finishes on the session it detached: if a Connecting handler disconnects
        /// and connects again, the teardown's cleanup and reconnect leave the new connect attempt
        /// alone. NoPing returns after the teardown and its reconnect decision; a send is the
        /// barrier for any connect they started.
        /// </summary>
        [Fact]
        public async Task TeardownDoesNotTouchConnectAttemptStartedByHandler()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var restarted = 0;
            var connecting = 0;
            client.Connecting += (_, _) =>
            {
                Interlocked.Increment(ref connecting);
                if (Interlocked.Exchange(ref restarted, 1) != 0) return;
                client.Disconnect();
                client.Connect();
            };
            NoPing.Fire(client);

            await client.ReadyAsync(Wait);
            await client.SendAsync(new byte[] { 1 });
            await Until(() => _server.Received.Any(c => c.Send != null));
            Assert.Equal(2, _server.Received.Count(c => c.Connect != null));
            Assert.Equal(2, Volatile.Read(ref connecting));
            Assert.Equal(CentrifugeClientState.Connected, client.State);
        }

        /// <summary>
        /// Ending a session fails the calls of a send stuck mid-write right away, without waiting
        /// for the write to give up: the server stops reading, so the RPC's write never completes.
        /// </summary>
        [Fact]
        public async Task SessionEndFailsCallsOfSendStuckInWrite()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.BeforeCommand = cmd =>
            {
                if (cmd.Publish == null) return Task.CompletedTask;
                held.TrySetResult();
                return release.Task;
            };
            try
            {
                var client = NewClient(new CentrifugeClientOptions { Timeout = TimeSpan.FromSeconds(30) });
                client.Connect();
                await client.ReadyAsync(Wait);
                _ = client.PublishAsync("channel", new byte[] { 1 });
                await held.Task.WaitAsync(Wait);

                var sendLock = (SemaphoreSlim)Field(NoPing.Transport(client)!, "_sendLock")!;
                var rpc = client.RpcAsync("method", new byte[64 * 1024 * 1024]);
                await Until(() => sendLock.CurrentCount == 0);
                NoPing.Fire(client);

                await Assert.ThrowsAnyAsync<CentrifugeException>(() => rpc.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>
        /// An unrecoverable-position error (112) answering a superseded attempt leaves the
        /// position the current attempt recovers from as it is.
        /// </summary>
        [Fact]
        public async Task StaleUnrecoverablePositionDoesNotResetNewAttempt()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var getStateCalls = 0;
            var options = WithoutBackoff();
            options.GetState = _ =>
            {
                Interlocked.Increment(ref getStateCalls);
                return Task.FromResult(new CentrifugeStreamPosition(7, "e"));
            };
            var sub = client.NewSubscription("stale-112", options);
            sub.Subscribe();
            var cmd = await held.Task.WaitAsync(Wait);
            sub.Unsubscribe();
            sub.Subscribe();
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 112, Message = "unrecoverable position" } });

            await sub.ReadyAsync(Wait);
            Assert.Equal(1, Volatile.Read(ref getStateCalls));
            Assert.Equal(7UL, _server.LastSubscribe()!.Offset);
        }

        /// <summary>
        /// A token-expired error (109) answering a superseded attempt doesn't make the current
        /// attempt fetch a new token.
        /// </summary>
        [Fact]
        public async Task StaleTokenExpiredDoesNotForceRefreshOfNewAttempt()
        {
            var held = HoldFirstSubscribe();
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var getTokenCalls = 0;
            var options = WithoutBackoff();
            options.Token = "t";
            options.GetToken = _ =>
            {
                Interlocked.Increment(ref getTokenCalls);
                return Task.FromResult("fresh");
            };
            var sub = client.NewSubscription("stale-109", options);
            sub.Subscribe();
            var cmd = await held.Task.WaitAsync(Wait);
            sub.Unsubscribe();
            sub.Subscribe();
            await _server.SendReplyAsync(new Reply { Id = cmd.Id, Error = new Error { Code = 109, Message = "token expired" } });

            await sub.ReadyAsync(Wait);
            Assert.Equal(0, Volatile.Read(ref getTokenCalls));
            Assert.Equal("t", _server.LastSubscribe()!.Token);
        }

        [Fact]
        public async Task SupersededGetTokenResultIsNotUsed()
        {
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var options = WithoutBackoff();
            options.GetToken = _ =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult("fresh");
                firstCall.TrySetResult();
                return releaseFirst.Task;
            };
            var sub = client.NewSubscription("get-token-result", options);
            sub.Subscribe();
            await firstCall.Task.WaitAsync(Wait);

            sub.Unsubscribe();
            sub.Subscribe();
            releaseFirst.TrySetResult("stale");

            await sub.ReadyAsync(Wait);
            Assert.Equal("fresh", _server.LastSubscribe()!.Token);
        }

        /// <summary>
        /// A subscription token refresh reply, or a temporary error, from an earlier session must
        /// not re-arm the refresh timer of the current session. An RPC sent after it is the
        /// barrier; by then no refresh timer may be set.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StaleSubRefreshOutcomeDoesNotRearmNewSession(bool temporaryError)
        {
            var refresh = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.OnCommand = cmd =>
                cmd.SubRefresh != null && refresh.TrySetResult(cmd) ? FakeCentrifugoServer.NoReply : null;
            var client = NewClient();
            client.Connect();
            await client.ReadyAsync(Wait);

            var subscribed = System.Threading.Channels.Channel.CreateUnbounded<bool>();
            var sub = client.NewSubscription("stale-refresh", new CentrifugeSubscriptionOptions
            {
                Token = "t",
                GetToken = _ => Task.FromResult("t"),
            });
            sub.Subscribed += (_, _) => subscribed.Writer.TryWrite(true);
            sub.Subscribe();
            await subscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            sub.RefreshToken();
            var cmd = await refresh.Task.WaitAsync(Wait);

            sub.Unsubscribe();
            sub.Subscribe();
            await subscribed.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            await _server.SendReplyAsync(temporaryError
                ? new Reply { Id = cmd.Id, Error = new Error { Code = 100, Message = "internal", Temporary = true } }
                : new Reply { Id = cmd.Id, SubRefresh = new SubRefreshResult { Expires = true, Ttl = 1 } });
            await client.RpcAsync("sync", default).WaitAsync(Wait);

            Assert.Null(Field(sub, "_refreshTimer"));
            Assert.Equal(1, _server.Received.Count(c => c.SubRefresh != null));
            Assert.Equal(CentrifugeSubscriptionState.Subscribed, sub.State);
        }
    }
}
