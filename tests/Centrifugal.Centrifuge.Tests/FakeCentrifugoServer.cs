using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>
    /// In-process Centrifugo fake server for tests, speaking the protobuf protocol
    /// over a WebSocket (Kestrel). It is intentionally protocol-level and generic:
    /// it provides sensible defaults for the connect/subscribe/unsubscribe
    /// handshake, captures received commands for assertions, and exposes hooks +
    /// raw push senders so new scenarios can be added WITHOUT touching the client
    /// under test or this helper.
    /// <para>
    /// This exists because some features (channel compaction, and in future others)
    /// are Centrifugo PRO only and can't be exercised against the OSS docker-compose
    /// server the other suites use; and because a fake gives deterministic control
    /// of timing, errors and reconnects.
    /// </para>
    /// <para>How to extend (most→least common):</para>
    /// <code>
    ///   // Customize a subscribe reply:
    ///   server.OnSubscribe = (channel, req) => new SubscribeResult { Recoverable = true };
    ///   // Negotiate channel compaction (assign a numeric id when the client offers it):
    ///   server.OnSubscribe = (channel, req) => new SubscribeResult { Id = (req.Flag &amp; 1) != 0 ? 42 : 0 };
    ///   // Push to a subscription:
    ///   await server.PublishIdAsync(42, data);            // by numeric id (compaction)
    ///   await server.PublishChannelAsync("news", data);   // by channel name
    ///   // Fully control any command reply (return null to fall through):
    ///   server.OnCommand = cmd => cmd.Rpc != null ? new Reply { Id = cmd.Id } : null;
    ///   // Send anything the protocol allows:
    ///   await server.SendPushAsync(new Push { Disconnect = new Disconnect { Code = 3000 } });
    ///   // Drive a reconnect:
    ///   server.CloseConnection();
    ///   // Assert on what the client sent:
    ///   server.Received / server.LastSubscribe()
    /// </code>
    /// </summary>
    internal sealed class FakeCentrifugoServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly object _lock = new();
        private readonly List<WebSocket> _sockets = new();
        private readonly List<Command> _received = new();
        private WebSocket? _current;

        /// <summary>connect reply; override to set expires/ttl/data/etc.</summary>
        public ConnectResult ConnectResult { get; set; } =
            new() { Client = "fake-client", Version = "0.0.0", Ping = 25 };

        /// <summary>
        /// Full override for any command — return a Reply to send, or null to fall
        /// through to default handling.
        /// </summary>
        public Func<Command, Reply?>? OnCommand { get; set; }

        /// <summary>Customize the subscribe result per channel (default: empty result).</summary>
        public Func<string, SubscribeRequest, SubscribeResult>? OnSubscribe { get; set; }

        public string Url { get; private set; } = "";

        /// <summary>A copy of all commands received from the client, in order.</summary>
        public IReadOnlyList<Command> Received
        {
            get { lock (_lock) { return new List<Command>(_received); } }
        }

        /// <summary>The most recent subscribe request the client sent, or null.</summary>
        public SubscribeRequest? LastSubscribe()
        {
            lock (_lock)
            {
                for (var i = _received.Count - 1; i >= 0; i--)
                {
                    if (_received[i].Subscribe != null) return _received[i].Subscribe;
                }
                return null;
            }
        }

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

        /// <summary>
        /// Close the active connection from the server side, triggering the client's
        /// automatic reconnect.
        /// </summary>
        public void CloseConnection()
        {
            WebSocket? ws;
            lock (_lock) { ws = _current; }
            try { ws?.Abort(); } catch { /* already gone */ }
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
                        await DispatchAsync(ws, cmd);
                    }
                }
            }
            catch
            {
                // Socket torn down during test shutdown — fine.
            }
        }

        private async Task DispatchAsync(WebSocket ws, Command cmd)
        {
            lock (_lock) { _received.Add(cmd); }

            if (OnCommand != null)
            {
                var reply = OnCommand(cmd);
                if (reply != null)
                {
                    await SendAsync(ws, reply);
                    return;
                }
            }

            if (cmd.Connect != null)
            {
                await SendAsync(ws, new Reply { Id = cmd.Id, Connect = ConnectResult });
            }
            else if (cmd.Subscribe != null)
            {
                var result = OnSubscribe != null
                    ? OnSubscribe(cmd.Subscribe.Channel, cmd.Subscribe)
                    : new SubscribeResult();
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

        // --- raw escape hatches -------------------------------------------------

        private static async Task SendAsync(WebSocket ws, Reply reply)
        {
            using var ms = new MemoryStream();
            reply.WriteDelimitedTo(ms);
            await ws.SendAsync(new ArraySegment<byte>(ms.ToArray()), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        /// <summary>Send a raw reply to the active connection.</summary>
        public Task SendReplyAsync(Reply reply)
        {
            WebSocket socket;
            lock (_lock)
            {
                socket = _current ?? throw new InvalidOperationException("no active connection");
            }
            return SendAsync(socket, reply);
        }

        /// <summary>Send a raw push (wrapped in a reply) to the active connection.</summary>
        public Task SendPushAsync(Push push) => SendReplyAsync(new Reply { Push = push });

        // --- typed push senders -------------------------------------------------
        //
        // Channel compaction pushes carry a numeric id and no channel; otherwise
        // the channel name is used. The *Id variants address by numeric id, the
        // *Channel variants by channel name.

        public Task PublishIdAsync(long id, byte[] data) =>
            SendPushAsync(new Push { Id = id, Pub = new Publication { Data = ByteString.CopyFrom(data) } });

        public Task PublishChannelAsync(string channel, byte[] data) =>
            SendPushAsync(new Push { Channel = channel, Pub = new Publication { Data = ByteString.CopyFrom(data) } });

        public Task JoinIdAsync(long id, string client) =>
            SendPushAsync(new Push { Id = id, Join = new Join { Info = new ClientInfo { Client = client } } });

        public Task LeaveIdAsync(long id, string client) =>
            SendPushAsync(new Push { Id = id, Leave = new Leave { Info = new ClientInfo { Client = client } } });

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
}
