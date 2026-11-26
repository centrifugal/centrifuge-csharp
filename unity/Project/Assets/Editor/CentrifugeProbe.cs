// Centrifuge connection probe for Unity: https://github.com/centrifugal/centrifuge-csharp/issues/35
//
// How to use:
//   1. Put this file in your project under Assets/Editor/ (create the folder if needed).
//   2. Set Endpoint (and Token if your server requires one) below.
//   3. In the Unity menu choose Centrifuge > Run connection probe.
//   4. Wait ~30 seconds. The report is copied to the clipboard and saved to Logs/centrifuge-probe.txt.
//      Paste it into the issue. The token is never printed.
//
// Headless: Unity -batchmode -nographics -projectPath <project> -executeMethod CentrifugeProbe.RunBatch -logFile -
// (PROBE_WS, PROBE_TOKEN and PROBE_CHANNEL environment variables override the settings below.)
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Centrifugal.Centrifuge.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class CentrifugeProbe
{
    // ---- Settings ----
    const string Endpoint = "ws://localhost:8000/connection/websocket";
    const string Token = "";   // connection JWT, if your server needs one
    const string Channel = ""; // optional: a channel this connection may subscribe and publish to

    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);
    static readonly StringBuilder Report = new StringBuilder();
    static readonly object ReportLock = new object();
    static readonly Stopwatch Clock = new Stopwatch();
    static bool _batch; // Application.isBatchMode, read on the main thread

    static string Setting(string env, string value) => Environment.GetEnvironmentVariable(env) ?? value;
    static string Ws => Setting("PROBE_WS", Endpoint);
    static string Tok => Setting("PROBE_TOKEN", Token);
    static string Chan => Setting("PROBE_CHANNEL", Channel);

    [MenuItem("Centrifuge/Run connection probe")]
    public static void RunFromMenu()
    {
        var context = Collect();
        Debug.Log("[probe] running, this takes up to ~30 seconds...");
        Task.Run(RunAllAsync).ContinueWith(t =>
        {
            if (t.Exception != null) Log("PROBE CRASHED: " + t.Exception);
            EditorApplication.delayCall += () => Finish(context);
        });
    }

    public static void RunBatch()
    {
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        var context = Collect();
        var code = 1;
        try
        {
            // The probe never needs the main thread, so blocking it here can't deadlock.
            code = Task.Run(RunAllAsync).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log("PROBE CRASHED: " + e);
        }
        Finish(context);
        EditorApplication.Exit(code);
    }

    /// <summary>Main-thread-only facts, read before the probe starts.</summary>
    static string Collect()
    {
        lock (ReportLock) Report.Clear();
        _batch = Application.isBatchMode;
        Clock.Restart();
        var s = new StringBuilder();
        s.AppendLine("unity: " + Application.unityVersion + (Application.isBatchMode ? " (batchmode)" : ""));
        s.AppendLine("enter play mode options: " + (EditorSettings.enterPlayModeOptionsEnabled
            ? EditorSettings.enterPlayModeOptions.ToString() : "disabled (domain reload on)"));
        s.AppendLine("scripting backend (standalone): " +
            PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone));
        return s.ToString();
    }

    static void Finish(string context)
    {
        string text;
        lock (ReportLock) text = "```\n" + context + Report + "```\n";
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", "centrifuge-probe.txt"), text);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[probe] could not save the report: " + e.Message);
        }
        if (!Application.isBatchMode) EditorGUIUtility.systemCopyBuffer = text;
        Debug.Log("[probe] done. Report " + (Application.isBatchMode ? "" : "copied to the clipboard and ") +
                  "saved to Logs/centrifuge-probe.txt\n" + text);
    }

    static void Log(string line)
    {
        line = $"[{Clock.ElapsedMilliseconds,6}ms] {line}";
        lock (ReportLock) Report.AppendLine(line);
        if (_batch) Debug.Log("[probe] " + line);
    }

    static async Task<int> RunAllAsync()
    {
        // Thread-pool latency is sampled through the whole run: the SDK handles replies on the pool,
        // so a starved pool looks exactly like a missing reply.
        using var pool = new PoolSampler();
        LogEnvironment();
        LogThreadPoolAndTls();
        await LogDnsAsync();
        await LogTlsAsync();
        Log("thread pool latency (idle): " + pool.Snapshot());
        await RawAsync("raw JSON, no subprotocol (does the socket work at all?)", null, receiveFirst: true);
        await RawAsync("raw protobuf, receive pending before send (as the SDK does)", "centrifuge-protobuf", receiveFirst: true);
        await RawAsync("raw protobuf, send before receive", "centrifuge-protobuf", receiveFirst: false);
        Log("thread pool latency (raw checks): " + pool.Snapshot());
        var ok = await ClientAsync();
        Log("thread pool latency (SDK check): " + pool.Snapshot());
        Log("RESULT: client " + (ok ? "OK" : "FAILED"));
        return ok ? 0 : 1;
    }

    static void LogThreadPoolAndTls()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        ThreadPool.GetAvailableThreads(out var availWorker, out var availIo);
        Log($"thread pool (worker/io): cpus={Environment.ProcessorCount} min={minWorker}/{minIo} " +
            $"max={maxWorker}/{maxIo} available={availWorker}/{availIo}");
        var callback = ServicePointManager.ServerCertificateValidationCallback;
        var owner = callback?.Method.DeclaringType;
        Log("global certificate validation callback: " +
            (owner == null ? "none" : "SET by " + owner.FullName + " (" + owner.Assembly.GetName().Name + ")"));
    }

    static async Task LogDnsAsync()
    {
        var host = new Uri(Ws).Host;
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            Log($"dns {host}: {string.Join(", ", addresses.Select(a => a.ToString()))} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception e)
        {
            Log($"dns {host}: FAILED after {sw.ElapsedMilliseconds}ms: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Logs the server certificate and chain of a wss:// connection. Called from the validation callback,
    /// so it must never throw.</summary>
    static void LogCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        try
        {
            Log("tls: policy errors=" + errors);
            if (certificate != null)
            {
                var leaf = new X509Certificate2(certificate);
                Log($"tls: leaf subject='{leaf.Subject}' issuer='{leaf.Issuer}' valid {leaf.NotBefore:u} .. {leaf.NotAfter:u} " +
                    $"sig={leaf.SignatureAlgorithm.FriendlyName}");
            }
            // Unity's TLS stack may hand over an empty X509Chain whose members throw.
            try
            {
                if (chain == null || chain.ChainElements == null || chain.ChainElements.Count == 0)
                {
                    Log("tls: chain not exposed by this TLS stack (the leaf issuer above names the CA)");
                    return;
                }
                foreach (var element in chain.ChainElements)
                {
                    var status = element.ChainElementStatus != null && element.ChainElementStatus.Length > 0
                        ? " status=" + string.Join(",", element.ChainElementStatus.Select(s => s.Status.ToString()))
                        : "";
                    Log("tls: chain '" + element.Certificate?.Subject + "'" + status);
                }
                if (chain.ChainStatus != null && chain.ChainStatus.Length > 0)
                    Log("tls: chain status: " + string.Join("; ", chain.ChainStatus.Select(s => s.Status + " " + s.StatusInformation?.Trim())));
            }
            catch (Exception)
            {
                Log("tls: chain not exposed by this TLS stack (the leaf issuer above names the CA)");
            }
        }
        catch (Exception e)
        {
            Log("tls: could not read the certificate: " + e.Message);
        }
    }

    static void LogEnvironment()
    {
        Log("endpoint: " + Ws + (Tok.Length > 0 ? " (with token)" : " (no token)"));
        Log("runtime: " + RuntimeInformation.FrameworkDescription + "; os: " + RuntimeInformation.OSDescription +
            "; process arch: " + RuntimeInformation.ProcessArchitecture + "; os arch: " + RuntimeInformation.OSArchitecture);
        try
        {
            var uri = new Uri(Ws.Replace("wss://", "https://").Replace("ws://", "http://"));
            var proxy = WebRequest.DefaultWebProxy;
            var via = proxy?.GetProxy(uri);
            Log("system proxy for endpoint: " + (proxy == null || via == null || via == uri ? "none" : via.ToString()));
        }
        catch (Exception e)
        {
            Log("system proxy: unknown (" + e.Message + ")");
        }
        Log("ClientWebSocket from: " + typeof(ClientWebSocket).Assembly.Location);
        var names = new[] { "Centrifugal.Centrifuge", "Google.Protobuf", "System.Threading.Channels",
            "System.Runtime.CompilerServices.Unsafe", "Microsoft.Extensions.Logging.Abstractions", "System.Memory" };
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => names.Contains(a.GetName().Name)).OrderBy(a => a.GetName().Name))
        {
            string location;
            try { location = asm.IsDynamic ? "dynamic" : asm.Location; } catch { location = "?"; }
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Log("loaded: " + asm.GetName().Name + " " + asm.GetName().Version + (info != null ? " (" + info + ")" : "") + " @ " + location);
        }
    }

    static async Task RawAsync(string title, string? subProtocol, bool receiveFirst)
    {
        Log("--- " + title + " ---");
        byte[] payload;
        WebSocketMessageType type;
        if (subProtocol == null)
        {
            var token = Tok.Length > 0 ? "\"token\":\"" + Tok + "\"" : "";
            payload = Encoding.UTF8.GetBytes("{\"id\":1,\"connect\":{" + token + "}}");
            type = WebSocketMessageType.Text;
        }
        else
        {
            var command = new Command { Id = 1, Connect = new ConnectRequest { Token = Tok } };
            using var ms = new MemoryStream();
            command.WriteDelimitedTo(ms);
            payload = ms.ToArray();
            type = WebSocketMessageType.Binary;
        }

        using var ws = new ClientWebSocket();
        if (subProtocol != null) ws.Options.AddSubProtocol(subProtocol);
        try
        {
            using (var cts = new CancellationTokenSource(StepTimeout))
                await ws.ConnectAsync(new Uri(Ws), cts.Token).ConfigureAwait(false);
            Log($"connected: state={ws.State} subProtocol='{ws.SubProtocol ?? "<null>"}' (asked '{subProtocol ?? "<none>"}')");

            var buffer = new byte[64 * 1024];
            using var receiveCts = new CancellationTokenSource();
            Task<WebSocketReceiveResult>? receive = null;
            if (receiveFirst)
            {
                receive = ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                await Task.Delay(50).ConfigureAwait(false); // let the read start waiting
            }

            using (var cts = new CancellationTokenSource(StepTimeout))
            {
                var send = ws.SendAsync(new ArraySegment<byte>(payload), type, true, cts.Token);
                if (await Task.WhenAny(send, Task.Delay(StepTimeout)).ConfigureAwait(false) != send)
                {
                    Log($"SEND DID NOT COMPLETE within {StepTimeout.TotalSeconds}s, state={ws.State}");
                    return;
                }
                await send.ConfigureAwait(false);
            }
            Log($"sent {payload.Length} bytes");

            receive ??= ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
            if (await Task.WhenAny(receive, Task.Delay(StepTimeout)).ConfigureAwait(false) != receive)
            {
                Log($"NO REPLY within {StepTimeout.TotalSeconds}s, state={ws.State}");
                receiveCts.Cancel();
                return;
            }
            var r = await receive.ConfigureAwait(false);
            Log($"received: type={r.MessageType} count={r.Count} end={r.EndOfMessage} " +
                $"closeStatus={r.CloseStatus?.ToString() ?? "-"} closeDesc='{r.CloseStatusDescription}'");
            if (r.MessageType == WebSocketMessageType.Text)
                Log("reply: " + Encoding.UTF8.GetString(buffer, 0, Math.Min(r.Count, 500)));
            else if (r.MessageType == WebSocketMessageType.Binary)
                Log("reply: " + DecodeReplies(buffer, r.Count));

            using (var cts = new CancellationTokenSource(StepTimeout))
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe done", cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log($"EXCEPTION state={ws.State}: {e}");
        }
    }

    /// <summary>For wss:// only: a plain TLS handshake with the server, to see the certificate chain and the
    /// platform's verdict. Unity's ClientWebSocket doesn't expose these (it ignores validation callbacks).</summary>
    static async Task LogTlsAsync()
    {
        var uri = new Uri(Ws);
        if (!string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) return;
        Log("--- TLS handshake (SslStream, outside the WebSocket) ---");
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(uri.Host, port);
            if (await Task.WhenAny(connect, Task.Delay(StepTimeout)).ConfigureAwait(false) != connect)
            {
                Log($"tls: TCP connect to port {port} did not complete within {StepTimeout.TotalSeconds}s");
                return;
            }
            await connect.ConfigureAwait(false);
            using var ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
            {
                LogCertificate(certificate, chain, errors);
                return errors == SslPolicyErrors.None;
            });
            var auth = ssl.AuthenticateAsClientAsync(uri.Host);
            if (await Task.WhenAny(auth, Task.Delay(StepTimeout)).ConfigureAwait(false) != auth)
            {
                Log($"tls: handshake did not complete within {StepTimeout.TotalSeconds}s");
                return;
            }
            await auth.ConfigureAwait(false);
            Log($"tls: handshake OK, protocol={ssl.SslProtocol} cipher={ssl.CipherAlgorithm} hash={ssl.HashAlgorithm}");
        }
        catch (Exception e)
        {
            Log("tls: handshake FAILED: " + e.GetType().Name + ": " + e.Message +
                (e.InnerException != null ? " | " + e.InnerException.GetType().Name + ": " + e.InnerException.Message : ""));
        }
    }

    static string DecodeReplies(byte[] data, int count)
    {
        try
        {
            var input = new CodedInputStream(data, 0, count);
            var sb = new StringBuilder();
            while (!input.IsAtEnd)
            {
                var reply = new Reply();
                input.ReadMessage(reply);
                sb.Append(reply);
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            return "<not decodable: " + e.Message + "> " + BitConverter.ToString(data, 0, Math.Min(count, 64));
        }
    }

    /// <summary>The SDK itself, with its logging on. Subscribes and publishes only if Channel is set.</summary>
    static async Task<bool> ClientAsync()
    {
        Log("--- CentrifugeClient ---");
        var options = new CentrifugeClientOptions { Logger = new ProbeLogger() };
        if (Tok.Length > 0) options.Token = Tok;
        using var client = new CentrifugeClient(Ws, options);
        client.Connecting += (_, e) => Log($"event Connecting: {e.Code} {e.Reason}");
        client.Connected += (_, e) => Log($"event Connected: client={e.ClientId} transport={e.Transport}");
        client.Disconnected += (_, e) => Log($"event Disconnected: {e.Code} {e.Reason}");
        client.Error += (_, e) => Log($"event Error: {e.Type} {e.Exception}");
        try
        {
            client.Connect();
            await client.ReadyAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            if (Chan.Length == 0) return true;

            var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sub = client.NewSubscription(Chan);
            sub.Publication += (_, e) => got.TrySetResult(true);
            sub.Subscribe();
            await sub.ReadyAsync(StepTimeout).ConfigureAwait(false);
            await client.PublishAsync(Chan, Encoding.UTF8.GetBytes("{\"probe\":true}")).ConfigureAwait(false);
            var received = await Task.WhenAny(got.Task, Task.Delay(StepTimeout)).ConfigureAwait(false) == got.Task;
            Log("publication " + (received ? "received" : "NOT received"));
            return received;
        }
        catch (Exception e)
        {
            Log($"client EXCEPTION: {e}");
            return false;
        }
        finally
        {
            client.Disconnect();
        }
    }

    sealed class ProbeLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            CentrifugeProbe.Log("sdk " + logLevel + ": " + formatter(state, exception) + (exception != null ? " | " + exception : ""));
        }
    }

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();
        public void Dispose() { }
    }

    /// <summary>Measures how long a work item queued to the thread pool waits before it runs, every 20 ms.
    /// It runs on its own thread, so a starved pool can't stall the measuring itself.</summary>
    sealed class PoolSampler : IDisposable
    {
        const int GiveUpMs = 5000;
        readonly object _gate = new object();
        readonly List<double> _samples = new List<double>();
        int _stuck;
        volatile bool _stop;

        public PoolSampler()
        {
            new Thread(Run) { IsBackground = true, Name = "probe-pool-sampler" }.Start();
        }

        void Run()
        {
            while (!_stop)
            {
                var sw = Stopwatch.StartNew();
                // Not disposed: after a give-up the work item may still set it later.
                var ran = new ManualResetEventSlim();
                ThreadPool.QueueUserWorkItem(_ => ran.Set());
                var ok = ran.Wait(GiveUpMs);
                lock (_gate)
                {
                    if (ok) _samples.Add(sw.Elapsed.TotalMilliseconds);
                    else _stuck++;
                }
                Thread.Sleep(20);
            }
        }

        /// <summary>Stats since the previous snapshot.</summary>
        public string Snapshot()
        {
            lock (_gate)
            {
                var s = _samples.OrderBy(x => x).ToList();
                var stuck = _stuck;
                _samples.Clear();
                _stuck = 0;
                if (s.Count == 0) return $"no samples, {stuck} not run within {GiveUpMs}ms";
                return $"{s.Count} samples, median {s[s.Count / 2]:0.0}ms, max {s[s.Count - 1]:0.0}ms, " +
                       $"over 100ms: {s.Count(x => x > 100)}, not run within {GiveUpMs}ms: {stuck}";
            }
        }

        public void Dispose() => _stop = true;
    }
}
