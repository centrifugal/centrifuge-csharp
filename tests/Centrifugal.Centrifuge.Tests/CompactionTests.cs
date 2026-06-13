using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// Minimal in-process Centrifugo stand-in speaking the protobuf protocol over
    /// a WebSocket (Kestrel). Channel compaction is a Centrifugo PRO feature, so it
    /// can't be exercised against the docker-compose OSS server — this fake
    /// negotiates a numeric channel ID in the subscribe reply and then sends pushes
    /// carrying the ID instead of the channel name, which is exactly what the real
    /// server does when compaction is enabled.
    /// </summary>
    internal sealed class FakeCompactionServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly object _lock = new();
        private readonly List<WebSocket> _sockets = new();
        private WebSocket? _current;
        private long _lastSubscribeFlag;

        /// <summary>flag value seen in the last SubscribeRequest.</summary>
        public long LastSubscribeFlag => Interlocked.Read(ref _lastSubscribeFlag);

        /// <summary>Numeric channel ID to assign on the next subscribe.</summary>
        public long NextChannelId = 42;

        public string Url { get; private set; } = "";

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();
            _app.UseWebSockets();
            _app.Map("/connection/websocket", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                var ws = await context.WebSockets.AcceptWebSocketAsync("centrifuge-protobuf");
                lock (_lock)
                {
                    _sockets.Add(ws);
                    _current = ws;
                }
                await ReceiveLoopAsync(ws);
            });
            await _app.StartAsync();
            var port = new Uri(_app.Urls.First()).Port;
            Url = $"ws://127.0.0.1:{port}/connection/websocket";
        }

        private async Task ReceiveLoopAsync(WebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    ms.Position = 0;
                    while (ms.Position < ms.Length)
                    {
                        var cmd = Command.Parser.ParseDelimitedFrom(ms);
                        await HandleCommandAsync(ws, cmd);
                    }
                }
            }
            catch
            {
                // Socket torn down during test shutdown — fine.
            }
        }

        private async Task HandleCommandAsync(WebSocket ws, Command cmd)
        {
            if (cmd.Connect != null)
            {
                await SendAsync(ws, new Reply
                {
                    Id = cmd.Id,
                    Connect = new ConnectResult { Client = "fake-client", Version = "0.0.0", Ping = 25 }
                });
            }
            else if (cmd.Subscribe != null)
            {
                Interlocked.Exchange(ref _lastSubscribeFlag, cmd.Subscribe.Flag);
                var result = new SubscribeResult();
                if ((cmd.Subscribe.Flag & 1) != 0)
                {
                    // Client offered channel compaction — assign a numeric channel ID.
                    result.Id = NextChannelId;
                }
                await SendAsync(ws, new Reply { Id = cmd.Id, Subscribe = result });
            }
            else if (cmd.Unsubscribe != null)
            {
                await SendAsync(ws, new Reply { Id = cmd.Id, Unsubscribe = new UnsubscribeResult() });
            }
            else if (cmd.Id != 0)
            {
                // Reply to anything else with an empty result to avoid client timeouts.
                await SendAsync(ws, new Reply { Id = cmd.Id });
            }
        }

        /// <summary>Send a publication push carrying only the numeric channel ID.</summary>
        public Task SendCompactedPubAsync(long id, byte[] data) =>
            SendToCurrentAsync(new Reply
            {
                Push = new Push { Id = id, Pub = new Publication { Data = ByteString.CopyFrom(data) } }
            });

        public Task SendCompactedJoinAsync(long id, string client) =>
            SendToCurrentAsync(new Reply
            {
                Push = new Push { Id = id, Join = new Join { Info = new ClientInfo { Client = client } } }
            });

        public Task SendCompactedLeaveAsync(long id, string client) =>
            SendToCurrentAsync(new Reply
            {
                Push = new Push { Id = id, Leave = new Leave { Info = new ClientInfo { Client = client } } }
            });

        private Task SendToCurrentAsync(Reply reply)
        {
            WebSocket socket;
            lock (_lock)
            {
                socket = _current ?? throw new InvalidOperationException("no active connection");
            }
            return SendAsync(socket, reply);
        }

        private static async Task SendAsync(WebSocket ws, Reply reply)
        {
            using var ms = new MemoryStream();
            reply.WriteDelimitedTo(ms);
            await ws.SendAsync(new ArraySegment<byte>(ms.ToArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            List<WebSocket> sockets;
            lock (_lock)
            {
                sockets = new List<WebSocket>(_sockets);
            }
            foreach (var socket in sockets)
            {
                try { socket.Abort(); } catch { /* already gone */ }
            }
            if (_app != null) await _app.DisposeAsync();
        }
    }

    [Collection("Integration")]
    public class CompactionTests : IAsyncLifetime
    {
        private readonly FakeCompactionServer _server = new();
        private CentrifugeClient? _client;

        public async Task InitializeAsync()
        {
            await _server.StartAsync();
            _client = new CentrifugeClient(_server.Url, new CentrifugeClientOptions());
        }

        public async Task DisposeAsync()
        {
            if (_client != null) await _client.DisposeAsync();
            await _server.DisposeAsync();
        }

        private async Task<CentrifugeSubscription> ConnectAndSubscribeAsync(
            System.Threading.Channels.Channel<CentrifugePublicationEventArgs> pubs,
            System.Threading.Channels.Channel<CentrifugeSubscribedEventArgs> subscribedEvents)
        {
            _client!.Connect();
            await _client.ReadyAsync();

            var sub = _client.NewSubscription("compacted-channel");
            sub.Subscribed += (_, e) => subscribedEvents.Writer.TryWrite(e);
            sub.Publication += (_, e) => pubs.Writer.TryWrite(e);
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return sub;
        }

        private static System.Threading.Channels.Channel<T> NewChannel<T>() =>
            System.Threading.Channels.Channel.CreateUnbounded<T>();

        [Fact]
        public async Task SubscribeOffersCompactionFlagAndPushesRoutedByNumericId()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            var joins = NewChannel<CentrifugeJoinEventArgs>();
            var leaves = NewChannel<CentrifugeLeaveEventArgs>();
            sub.Join += (_, e) => joins.Writer.TryWrite(e);
            sub.Leave += (_, e) => leaves.Writer.TryWrite(e);

            // The subscribe request must carry the channelCompaction bit.
            Assert.Equal(1, _server.LastSubscribeFlag & 1);

            // Publication push with numeric ID only (no channel name).
            await _server.SendCompactedPubAsync(42, Encoding.UTF8.GetBytes("{\"compacted\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"compacted\":true}", Encoding.UTF8.GetString(pub.Data.Span));

            // Join / leave pushes are compacted the same way.
            await _server.SendCompactedJoinAsync(42, "other-client");
            var join = await joins.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other-client", join.Info.Client);

            await _server.SendCompactedLeaveAsync(42, "other-client");
            var leave = await leaves.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other-client", leave.Info.Client);
        }

        [Fact]
        public async Task PushWithUnknownIdIsDroppedWithoutAffectingOthers()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            // Unknown ID — must not be delivered anywhere (and must not crash).
            await _server.SendCompactedPubAsync(99, Encoding.UTF8.GetBytes("{\"stray\":true}"));
            // Known ID delivered after the stray one (ordered same socket) proves
            // the client processed and dropped the stray push.
            await _server.SendCompactedPubAsync(42, Encoding.UTF8.GetBytes("{\"ok\":true}"));

            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "stray push with unknown id was delivered");
        }

        [Fact]
        public async Task IdMappingDroppedOnUnsubscribeAndRefreshedOnResubscribe()
        {
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            var sub = await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            var unsubscribedEvents = NewChannel<CentrifugeUnsubscribedEventArgs>();
            sub.Unsubscribed += (_, e) => unsubscribedEvents.Writer.TryWrite(e);
            sub.Unsubscribe();
            await unsubscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // Old ID must no longer route to the unsubscribed subscription.
            await _server.SendCompactedPubAsync(42, Encoding.UTF8.GetBytes("{\"stale\":true}"));

            // Resubscribe — the server assigns a fresh ID.
            _server.NextChannelId = 43;
            sub.Subscribe();
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await _server.SendCompactedPubAsync(43, Encoding.UTF8.GetBytes("{\"fresh\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"fresh\":true}", Encoding.UTF8.GetString(pub.Data.Span));
            Assert.False(pubs.Reader.TryRead(out _), "stale push delivered after unsubscribe");
        }

        [Fact]
        public async Task SameIdReRegisteredAfterDisconnectReconnect()
        {
            // Regression guard (found in the dart port): the client drops the ID
            // registry on transport teardown (IDs are server-session-scoped), and on
            // reconnect the server commonly assigns the SAME ID to the channel again.
            // The subscription must re-register it even though its own remembered ID
            // is unchanged.
            var pubs = NewChannel<CentrifugePublicationEventArgs>();
            var subscribedEvents = NewChannel<CentrifugeSubscribedEventArgs>();
            await ConnectAndSubscribeAsync(pubs, subscribedEvents);

            _client!.Disconnect();

            _client.Connect();
            // Subscription auto-resubscribes on reconnect — wait for the second
            // Subscribed event. Same ID 42 as before the reconnect.
            await subscribedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await _server.SendCompactedPubAsync(42, Encoding.UTF8.GetBytes("{\"after_reconnect\":true}"));
            var pub = await pubs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"after_reconnect\":true}", Encoding.UTF8.GetString(pub.Data.Span));
        }
    }
}
