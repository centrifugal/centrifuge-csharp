// Smoke tests of the SDK on Unity's runtime (Mono, Unity's class libraries and TLS stack), against the
// Centrifugo from the repo's docker-compose.yml. Run with `make unity-test` (see unity/README.md), or from
// the Editor menu Centrifuge > Run smoke tests.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class CentrifugeUnityTests
{
    const string WebSocketEndpoint = "ws://localhost:8000/connection/websocket";
    const string HttpStreamEndpoint = "http://localhost:8000/connection/http_stream";
    const string ApiEndpoint = "http://localhost:8000/api";
    const string HmacSecret = "secret"; // CENTRIFUGO_CLIENT_TOKEN_HMAC_SECRET_KEY in docker-compose.yml

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly HttpClient Http = new HttpClient();

    static readonly (string Name, CentrifugeTransportType Type, string Endpoint)[] Transports =
    {
        ("websocket", CentrifugeTransportType.WebSocket, WebSocketEndpoint),
        ("http_stream", CentrifugeTransportType.HttpStream, HttpStreamEndpoint),
    };

    public static void RunBatch()
    {
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        EditorApplication.Exit(Run() ? 0 : 1);
    }

    [MenuItem("Centrifuge/Run smoke tests")]
    public static void RunFromMenu() => Run();

    /// <summary>Runs every test and logs a summary. Blocks the calling (main) thread: the SDK never needs
    /// it, and one test checks exactly that by disposing a client on it.</summary>
    static bool Run()
    {
        Debug.Log($"[unity-test] Unity {Application.unityVersion}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        var results = new List<(string Name, string? Error, long Ms)>();
        foreach (var (name, type, endpoint) in Transports)
        {
            Execute(results, name + ": connect, subscribe, publish", () => ConnectSubscribePublishAsync(type, endpoint));
            Execute(results, name + ": history", () => HistoryAsync(type, endpoint));
            Execute(results, name + ": presence", () => PresenceAsync(type, endpoint));
            Execute(results, name + ": reconnect and resubscribe after server disconnect", () => ReconnectAfterServerDisconnectAsync(type, endpoint));
            Execute(results, name + ": Dispose on the main thread", () => DisposeOnMainThread(type, endpoint));
        }

        var failed = results.Count(r => r.Error != null);
        var summary = new StringBuilder();
        summary.AppendLine($"[unity-test] {results.Count - failed} passed, {failed} failed (Unity {Application.unityVersion})");
        foreach (var r in results)
            summary.AppendLine($"  {(r.Error == null ? "PASS" : "FAIL")}  {r.Name} ({r.Ms}ms){(r.Error == null ? "" : "\n        " + r.Error)}");
        if (failed == 0) Debug.Log(summary.ToString());
        else Debug.LogError(summary.ToString());
        return failed == 0;
    }

    static void Execute(List<(string, string?, long)> results, string name, Func<Task> test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Off the main thread, as application code awaiting the SDK from a MonoBehaviour would be.
            Task.Run(test).GetAwaiter().GetResult();
            results.Add((name, null, sw.ElapsedMilliseconds));
        }
        catch (Exception e)
        {
            results.Add((name, e.GetType().Name + ": " + e.Message, sw.ElapsedMilliseconds));
        }
    }

    static void Execute(List<(string, string?, long)> results, string name, Action test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            test();
            results.Add((name, null, sw.ElapsedMilliseconds));
        }
        catch (Exception e)
        {
            results.Add((name, e.GetType().Name + ": " + e.Message, sw.ElapsedMilliseconds));
        }
    }

    // ---- Tests ----

    static async Task ConnectSubscribePublishAsync(CentrifugeTransportType type, string endpoint)
    {
        using var client = NewClient(type, endpoint);
        client.Connect();
        await client.ReadyAsync(Wait);

        var channel = UniqueChannel();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = client.NewSubscription(channel);
        sub.Publication += (_, e) => received.TrySetResult(Encoding.UTF8.GetString(e.Data.ToArray()));
        sub.Subscribe();
        await sub.ReadyAsync(Wait);

        await client.PublishAsync(channel, Encoding.UTF8.GetBytes("{\"n\":1}"));
        var data = await WithTimeout(received.Task, "publication");
        Expect(data == "{\"n\":1}", "unexpected publication data: " + data);
    }

    static async Task HistoryAsync(CentrifugeTransportType type, string endpoint)
    {
        using var client = NewClient(type, endpoint);
        client.Connect();
        await client.ReadyAsync(Wait);

        var channel = UniqueChannel();
        for (var i = 1; i <= 3; i++)
            await client.PublishAsync(channel, Encoding.UTF8.GetBytes("{\"n\":" + i + "}"));

        var history = await client.HistoryAsync(channel, new CentrifugeHistoryOptions { Limit = 10 });
        var data = history.Publications.Select(p => Encoding.UTF8.GetString(p.Data.ToArray())).ToArray();
        Expect(data.SequenceEqual(new[] { "{\"n\":1}", "{\"n\":2}", "{\"n\":3}" }),
            "unexpected history: [" + string.Join(", ", data) + "]");
        Expect(history.Offset == 3, "unexpected history offset " + history.Offset);
    }

    static async Task PresenceAsync(CentrifugeTransportType type, string endpoint)
    {
        using var client = NewClient(type, endpoint);
        string? clientId = null;
        client.Connected += (_, e) => clientId = e.ClientId;
        client.Connect();
        await client.ReadyAsync(Wait);

        var channel = UniqueChannel();
        var sub = client.NewSubscription(channel);
        sub.Subscribe();
        await sub.ReadyAsync(Wait);

        var presence = await client.PresenceAsync(channel);
        Expect(clientId != null && presence.Clients.ContainsKey(clientId), "own client missing from presence");
        var stats = await client.PresenceStatsAsync(channel);
        Expect(stats.NumClients == 1, "unexpected presence stats: " + stats.NumClients + " clients");
    }

    static async Task ReconnectAfterServerDisconnectAsync(CentrifugeTransportType type, string endpoint)
    {
        var user = "unity-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var client = NewClient(type, endpoint, Jwt(user));
        var connected = 0;
        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connected += (_, _) => { if (Interlocked.Increment(ref connected) == 2) reconnected.TrySetResult(true); };
        client.Connect();
        await client.ReadyAsync(Wait);

        var channel = UniqueChannel();
        var subscribed = 0;
        var resubscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = client.NewSubscription(channel);
        sub.Subscribed += (_, _) => { if (Interlocked.Increment(ref subscribed) == 2) resubscribed.TrySetResult(true); };
        sub.Publication += (_, _) => received.TrySetResult(true);
        sub.Subscribe();
        await sub.ReadyAsync(Wait);

        // Code 4000 is in the range a client reconnects after (4000-4499).
        await ServerApiAsync("disconnect",
            "{\"user\":\"" + user + "\",\"disconnect\":{\"code\":4000,\"reason\":\"unity test\"}}");
        await WithTimeout(reconnected.Task, "reconnect");
        await WithTimeout(resubscribed.Task, "resubscribe");

        await client.PublishAsync(channel, Encoding.UTF8.GetBytes("{}"));
        await WithTimeout(received.Task, "publication after reconnect");
    }

    /// <summary>The README's Unity example disposes the client in OnDestroy, on the main thread: Dispose
    /// blocks until the disconnect completes, so it must not need the main thread.</summary>
    static void DisposeOnMainThread(CentrifugeTransportType type, string endpoint)
    {
        var client = NewClient(type, endpoint);
        client.Connect();
        client.ReadyAsync(Wait).GetAwaiter().GetResult();
        var sw = Stopwatch.StartNew();
        client.Dispose();
        Expect(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.ElapsedMilliseconds}ms");
    }

    // ---- Helpers ----

    static CentrifugeClient NewClient(CentrifugeTransportType type, string endpoint, string? token = null)
    {
        var options = new CentrifugeClientOptions();
        if (token != null) options.Token = token;
        return new CentrifugeClient(new[] { new CentrifugeTransportEndpoint(type, endpoint) }, options);
    }

    static string UniqueChannel() => "unity_" + Guid.NewGuid().ToString("N").Substring(0, 12);

    static async Task<T> WithTimeout<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(Wait)) != task)
            throw new TimeoutException($"no {what} within {Wait.TotalSeconds}s");
        return await task;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static async Task ServerApiAsync(string method, string json)
    {
        using var response = await Http.PostAsync(ApiEndpoint + "/" + method, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        Expect(response.IsSuccessStatusCode && !body.Contains("\"error\""), $"server API {method} failed: {(int)response.StatusCode} {body}");
    }

    /// <summary>An HS256 connection token for <paramref name="user"/>, without expiry.</summary>
    static string Jwt(string user)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = B64(Encoding.UTF8.GetBytes("{\"sub\":\"" + user + "\"}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacSecret));
        var signature = B64(hmac.ComputeHash(Encoding.UTF8.GetBytes(header + "." + payload)));
        return header + "." + payload + "." + signature;
    }
}
