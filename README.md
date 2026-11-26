# Centrifuge C# SDK

[![NuGet](https://img.shields.io/nuget/v/Centrifugal.Centrifuge.svg)](https://www.nuget.org/packages/Centrifugal.Centrifuge/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

C# client SDK for [Centrifugo](https://github.com/centrifugal/centrifugo) and [Centrifuge](https://github.com/centrifugal/centrifuge) real-time messaging servers.

> **Status:** the SDK is in 0.x, so its API may still change before 1.0. Feedback and issue reports are welcome.

## Features

- ✅ Multi-platform support
- ✅ WebSocket and HTTP-streaming transports with automatic fallback
- ✅ Browser-native transports for Blazor WebAssembly (using JS interop)
- ✅ Protobuf binary protocol for efficient communication
- ✅ Automatic command batching for improved network efficiency
- ✅ Automatic reconnection with exponential backoff and full jitter
- ✅ Channel subscriptions with recovery and Fossil delta compression support
- ✅ Server-side publication filtering based on tags
- ✅ JWT authentication with automatic token refresh
- ✅ Publications and RPC calls
- ✅ Presence and presence stats, join/leave events
- ✅ History API
- ✅ Thread-safe

## Platform Support

The library targets modern .NET frameworks:

- .NET 10.0
- .NET 9.0
- .NET 8.0
- .NET 6.0
- .NET Standard 2.1 (for Unity support)

This means it works on:

- ✅ **Server-side**: .NET 6/8/9/10
- ✅ **Desktop**: Windows, macOS, Linux (via modern .NET)
- ✅ **Mobile**: .NET MAUI (iOS, Android)
- ✅ **Web**: Blazor WebAssembly, Blazor Server
- ✅ **Unity**: 2021.2+ with .NET Standard 2.1 support

> **Note**: UWP is not recommended for new projects. Consider using Windows App SDK / WinUI 3 instead, which work with modern .NET targets.

## Transport Compatibility

The SDK supports multiple transport protocols with platform-specific implementations for optimal performance:

| Transport | Server/.NET Desktop | Blazor Server | Blazor WASM | Unity Desktop/Mobile | Unity WebGL |
|-----------|---------------------|---------------|-------------|----------------------|-------------|
| **WebSocket** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ❌ Not Supported |
| **HTTP Stream** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ❌ Not Supported |

**\*Blazor WebAssembly**: Requires `builder.Services.AddCentrifugeClient()` in `Program.cs`. The SDK automatically uses browser-native implementations via JavaScript interop for both WebSocket and HTTP Stream transports.

**Unity WebGL**: Not supported. WebGL builds have no `System.Net` networking, and the SDK doesn't accept custom transports, so third-party WebSocket plugins can't be used with it.

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package Centrifugal.Centrifuge
```

Or via Package Manager Console:

```powershell
Install-Package Centrifugal.Centrifuge
```

## Quick Start

### Basic Connection

```csharp
using Centrifugal.Centrifuge;

// Recommended: Use 'await using' for proper async disposal
await using var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

// Setup event handlers
client.Connecting += (sender, e) =>
{
    Console.WriteLine($"[Client] Connecting: {e.Code} - {e.Reason}");
};

client.Connected += (sender, e) =>
{
    Console.WriteLine($"Connected with client ID: {e.ClientId}");
};

client.Disconnected += (sender, e) =>
{
    Console.WriteLine($"Disconnected: {e.Code} - {e.Reason}");
};

client.Error += (sender, e) =>
{
    Console.WriteLine($"Error: {e.Message}");
};

// Connect to server (non-blocking)
client.Connect();

// Use client methods - they automatically wait for connection
var result = await client.RpcAsync("method", data);

// DisposeAsync is called automatically at the end of the 'await using' block
// It waits for disconnect to complete before releasing resources
```

## Event Handlers and Callbacks

### ⚠️ Important: Use Async Event Handlers

The state of a client and its subscriptions changes on one **event loop** per client: an application call, a frame received from the server, a timer or the result of a callback (`GetToken`, `GetState`) is one step, and steps run one at a time, in order. Event handlers run **synchronously** inside the step that raises them, so your application observes events in the order things happened: `Connected` (with the server-side subscription events and recovered publications it carries) and a subscription's `Subscribed` (followed by its recovered publications) are raised before any publication received after the corresponding reply, and a handler never runs concurrently with another handler of the same client.

The loop isn't a dedicated thread: a step runs on the thread that brings it (your thread for a call, a transport's or the thread pool's otherwise) when the loop is idle, or right after the step in progress. The SDK never blocks your thread waiting for the loop: a call made from another thread while a handler runs takes effect after that handler returns.

A call into the SDK from a handler (e.g. `sub.Unsubscribe()`, `client.Disconnect()`) takes effect at once, before it returns — as in centrifuge-js. For best performance and responsiveness, keep callbacks fast or use `async void` event handlers:

```csharp
client.Connected += async (sender, e) =>
{
    Console.WriteLine($"Connected: {e.ClientId}");
    await some_call();
};
```

**Can you use blocking calls like `.Wait()` or `.Result`?**

**NO - This will cause a deadlock!** Never block on SDK async methods in event handlers:

```csharp
client.Connected += (sender, e) =>
{
    // ❌ DEADLOCK! Never use .Wait(), .Result, or .GetAwaiter().GetResult()
    client.PublishAsync("channel", data).Wait();          // DEADLOCK!
    var result = client.RpcAsync("method", data).Result;  // DEADLOCK!
};
```

**Why does this deadlock?** The handler runs inside a step of the client's loop. `PublishAsync()` completes when its reply is processed — by a later step of the same loop, which can't run while your handler blocks it.

While a handler runs, the client processes nothing else: no replies, no pings, no publications of other subscriptions, no timers. So any synchronous wait in a handler for something that needs the client deadlocks or stalls it — including marshalling synchronously (`Dispatcher.Invoke`, `Control.Invoke`, `SynchronizationContext.Send`) to a thread that waits for the SDK, or taking a lock that a thread calling the SDK may hold while it waits for the reply. A call from another thread made while a handler runs doesn't wait for it: it takes effect right after the handler returns, in call order — so don't read `State` right after such a call to learn its outcome; use the events or `ReadyAsync()`. Replies that arrive while a handler runs are processed after it returns; a reply already received when its timeout fires is still delivered. Keep handlers fast, and post to the UI thread asynchronously (`BeginInvoke`, `SynchronizationContext.Post`).

Calling `Disconnect()`, `Dispose()` or `DisposeAsync()` from a handler is safe: they take effect at once and don't wait for the connection to close.

**Signalling Your Code from Event Handlers**

A `TaskCompletionSource` that a handler completes should be created with `TaskCreationOptions.RunContinuationsAsynchronously`. Without it, the code awaiting its task continues inline, inside the handler: it holds the client's loop until it yields, and deadlocks if it blocks on an SDK method:

```csharp
var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
subscription.Subscribed += (sender, e) => subscribed.TrySetResult(true);
```

**Exception Handling in Event Handlers**

Event handlers should not throw: catch exceptions in your handlers. The SDK can't know what a throwing handler left undone, so it fails loudly instead of going on as if nothing happened:

* the exception is reported through the `Error` event of the client or subscription, with `Type` set to the name of the failing event (`publication`, `subscribing`, ...);
* the client disconnects with code `2` (bad protocol) and the reason `exception in event handler: <message>`, and doesn't reconnect: call `Connect()` to start again;
* the handlers after the failing one don't get that event; the SDK's own work completes as if the handler had returned, so the client is left in a consistent state;
* a publication whose handler threw doesn't count as delivered: after `Connect()`, a recoverable subscription receives it again.

An exception in the SDK's own processing (an SDK bug, or a broken environment such as a dependency missing at run time) is handled the same way: it is reported through the client's `Error` event with `Type` set to `internal`, and the client disconnects with code `2` and the reason `internal error: <message>`, without reconnecting. When it happens within an SDK call made from an event handler, the call throws into the handler, so it is reported as that handler's exception.

An exception thrown by an `Error` handler is logged, and stops the client as well. An `async void` handler that throws after its first `await` is out of the SDK's reach, and the exception crashes the application. Catch exceptions yourself:

```csharp
// ❌ BAD: throws after the first await - crashes the application
subscription.Publication += async (sender, e) =>
{
    await riskyOperation();
};

// ✅ GOOD: Always wrap your logic in try-catch
subscription.Publication += async (sender, e) =>
{
    try
    {
        await riskyOperation();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing publication: {ex.Message}");
        // Log, report to error tracking service, etc.
    }
};
```

**Best Practices Summary**

1. ✅ Use `async void` for event handlers that need to perform async operations
2. ✅ Always `await` async methods - never block with `.Wait()`, `.Result`, or `.GetAwaiter().GetResult()`
3. ✅ Always wrap `async void` handlers in `try-catch` to prevent application crashes
4. ✅ Keep handlers fast - heavy CPU work should be offloaded to background tasks
5. ✅ Event order is preserved - handlers are invoked synchronously, one at a time, on the client's event loop
6. ✅ Pass `TaskCreationOptions.RunContinuationsAsynchronously` to any `TaskCompletionSource` a handler completes

## Subscriptions

```csharp
// Create subscription, may throw CentrifugeDuplicateSubscriptionException
// if subscription to the channel already exists in Client's registry.
var subscription = client.NewSubscription("chat");

// Setup subscription event handlers
subscription.Publication += (sender, e) =>
{
    var data = Encoding.UTF8.GetString(e.Data.Span);
    Console.WriteLine($"Message from {e.Channel}: {data}");
};

subscription.Subscribing += (sender, e) =>
{
    Console.WriteLine("Subscribing to channel");
};

subscription.Subscribed += (sender, e) =>
{
    Console.WriteLine($"Subscribed to channel, recovered: {e.Recovered}");
};

subscription.Unsubscribed += (sender, e) =>
{
    Console.WriteLine($"Unsubscribed: {e.Code} - {e.Reason}");
};

// Subscribe to channel (non-blocking)
subscription.Subscribe();

// Publish to channel - automatically waits for subscription to be ready
var message = Encoding.UTF8.GetBytes("Hello, world!");
await subscription.PublishAsync(message);

// Unsubscribe when done
subscription.Unsubscribe();
```

To drop a subscription for good, call `subscription.Dispose()` or `client.RemoveSubscription(subscription)`. The
client forgets it immediately, so the channel can be subscribed again with a new subscription. It is then
unsubscribed the same way `Unsubscribe()` does it: an unsubscribe command goes to the server if the server may hold
the subscription, followed by `StateChanged` and `Unsubscribed` (a handler of these may already create the new
subscription). Once disposed, a subscription can't be subscribed again.

### Token Authentication

```csharp
var options = new CentrifugeClientOptions
{
    // Provide initial token
    Token = "your-jwt-token",

    // Optional: provide token refresh callback
    GetToken = async () =>
    {
        // Fetch new token from your backend
        var response = await httpClient.GetAsync("https://your-backend.com/centrifugo/token");
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return tokenResponse.Token;
    }
};

var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
```

### Channel-Specific Token

```csharp
var subscriptionOptions = new CentrifugeSubscriptionOptions
{
    Token = "channel-specific-jwt",

    GetToken = async (channel) =>
    {
        // Fetch channel-specific token from your backend
        var response = await httpClient.PostAsJsonAsync(
            "https://your-backend.com/centrifugo/subscription_token",
            new { channel }
        );
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return tokenResponse.Token;
    }
};

var subscription = client.NewSubscription("private-channel", subscriptionOptions);
subscription.Subscribe();
```

### History API

```csharp
// Get last 10 messages
var history = await subscription.HistoryAsync(new CentrifugeHistoryOptions
{
    Limit = 10
});

foreach (var pub in history.Publications)
{
    var data = Encoding.UTF8.GetString(pub.Data.Span);
    Console.WriteLine($"Message: {data}");
}
```

### Presence API

```csharp
// Get all clients in channel
var presence = await subscription.PresenceAsync();
foreach (var client in presence.Clients.Values)
{
    Console.WriteLine($"Client: {client.Client}, User: {client.User}");
}

// Get presence stats (counts only)
var stats = await subscription.PresenceStatsAsync();
Console.WriteLine($"Clients: {stats.NumClients}, Users: {stats.NumUsers}");
```

### RPC Calls

```csharp
var request = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
var result = await client.RpcAsync("my_method", request);
var response = Encoding.UTF8.GetString(result.Data.Span);
Console.WriteLine($"RPC result: {response}");
```

### Multi-Transport Fallback

For environments where WebSocket might be blocked, use automatic transport fallback:

```csharp
using Centrifugal.Centrifuge;

// Define transport endpoints to try in order
var transports = new[]
{
    new CentrifugeTransportEndpoint(
        CentrifugeTransportType.WebSocket,
        "ws://localhost:8000/connection/websocket"
    ),
    new CentrifugeTransportEndpoint(
        CentrifugeTransportType.HttpStream,
        "http://localhost:8000/connection/http_stream"
    )
};

// Create client with transport fallback
var client = new CentrifugeClient(transports);

// Client will try WebSocket first, then fall back to HTTP Stream if needed
client.Connect();
```

**Note**: In Blazor WebAssembly, ensure you've called `builder.Services.AddCentrifugeClient()` first (see Blazor Support section). The SDK will automatically use browser-native transports.

### Advanced Configuration

```csharp
var options = new CentrifugeClientOptions
{
    // Connection token and refresh
    Token = "your-jwt-token",
    GetToken = async () => await FetchTokenAsync(),

    // Connection data
    Data = Encoding.UTF8.GetBytes("{\"custom\":\"data\"}"),

    // Client identification (used for observability)
    Name = "my-app",
    Version = "1.0.0",

    // Reconnection settings
    MinReconnectDelay = TimeSpan.FromMilliseconds(500),
    MaxReconnectDelay = TimeSpan.FromSeconds(20),

    // Timeouts: Timeout bounds a transport open, the wait for a connection (calls made while
    // connecting) and then a command's reply, from when it is sent (as centrifuge-js)
    Timeout = TimeSpan.FromSeconds(5),
    MaxServerPingDelay = TimeSpan.FromSeconds(10),

    // Logging (pass ILogger for debug output)
    Logger = loggerFactory.CreateLogger<CentrifugeClient>(),

    // Custom headers (works over header emulation, requires Centrifugo v6+)
    Headers = new Dictionary<string, string>
    {
        ["X-Custom-Header"] = "value"
    }
};

var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
```

Creating a client or subscription with invalid options throws `CentrifugeConfigurationException`. The checks:

- no delay is negative, and no maximum delay is smaller than its minimum;
- `Timeout` is positive;
- `Timeout`, `MaxReconnectDelay` and the subscription `MaxResubscribeDelay` fit in `Int32.MaxValue` milliseconds;
- for an HTTP streaming endpoint, `EmulationEndpoint` is an absolute http/https URL (in the browser it, like the
  fallback endpoints, may also be a page-relative URL).

### Recovery and Positioning

```csharp
var options = new CentrifugeSubscriptionOptions
{
    // Enable recovery (if supported by server)
    Recoverable = true,

    // Enable positioning
    Positioned = true,

    // Start from known position
    Since = new CentrifugeStreamPosition(offset: 100, epoch: "epoch-id")
};

var subscription = client.NewSubscription("chat", options);

subscription.Subscribed += (sender, e) =>
{
    if (e.WasRecovering)
    {
        Console.WriteLine($"Recovery attempted, recovered: {e.Recovered}");
    }
};

subscription.Subscribe();
```

### Server-Side Publication Filtering

Filter publications on the server side based on publication tags, so unwanted messages
never reach the client. Build filter expressions with `CentrifugeFilterNodeBuilder`
and pass them via `TagsFilter` (or `SetTagsFilter` to change the filter later). Cannot
be used together with delta compression.

```csharp
var options = new CentrifugeSubscriptionOptions
{
    // Only deliver publications tagged with ticker=BTC and price > 50000
    TagsFilter = CentrifugeFilterNodeBuilder.And(
        CentrifugeFilterNodeBuilder.Eq("ticker", "BTC"),
        CentrifugeFilterNodeBuilder.Gt("price", "50000")
    )
};

var subscription = client.NewSubscription("prices", options);
subscription.Subscribe();

// Change the filter later - applied on the next (re)subscription attempt
subscription.SetTagsFilter(CentrifugeFilterNodeBuilder.In("ticker", "BTC", "ETH", "SOL"));
```

Available comparisons: `Eq`, `Neq`, `In`, `Nin`, `Ex`, `Nex`, `StartsWith`, `EndsWith`,
`Contains`, `Lt`, `Lte`, `Gt`, `Gte`, combined with `And`, `Or`, `Not`.

## Error Handling

```csharp
try
{
    await client.RpcAsync("method", data);
}
catch (CentrifugeUnauthorizedException)
{
    Console.WriteLine("Authentication failed");
}
catch (CentrifugeTimeoutException)
{
    Console.WriteLine("Operation timed out");
}
catch (CentrifugeException ex)
{
    Console.WriteLine($"Centrifuge error: {ex.Code} - {ex.Message}");
    Console.WriteLine($"Temporary: {ex.Temporary}");
}
```

A `CentrifugeTimeoutException` comes either from waiting for the client or subscription to become ready
(`ReadyAsync`, which the calls also await) or from a reply that didn't come within `Timeout`. In the second case the
command may or may not have reached the server. Other failures use `CentrifugeException` codes: `TransportWriteError`
when the command couldn't be written, `ConnectionClosed` when the connection went away before the reply.

## Working with JSON Data

The SDK uses binary data (`ReadOnlyMemory<byte>`) for all payloads, but this doesn't mean you can't use JSON. JSON is simply a UTF-8 encoded byte sequence, so it works seamlessly:

```csharp
// Sending JSON
var message = new { text = "Hello", timestamp = DateTime.UtcNow };
var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message);
await subscription.PublishAsync(jsonBytes);

// Receiving JSON
subscription.Publication += (sender, e) =>
{
    // Option 1: Deserialize directly from bytes (zero-copy, recommended)
    var message = JsonSerializer.Deserialize<MyMessage>(e.Data.Span);

    // Option 2: Convert to string first
    var json = Encoding.UTF8.GetString(e.Data.Span);
    Console.WriteLine($"Received: {json}");
};
```

Using `ReadOnlyMemory<byte>` instead of `string` provides flexibility - you can work with JSON, MessagePack, Protobuf, or any other serialization format. The `.Span` property enables zero-copy parsing with modern serializers like `System.Text.Json`.

## Design Notes

### Non-Async Connect and Subscribe Methods

You may notice that `Connect()` and `Subscribe()` methods are not async and don't return a `Task`. This is an intentional design choice:

```csharp
client.Connect();           // Returns void, not Task
subscription.Subscribe();   // Returns void, not Task
```

**Why?**

1. **Lifecycle Management**: The SDK manages connection and subscription lifecycle internally, including automatic reconnection and resubscription. Calling `Connect()` expresses intent ("I want to be connected"), and the SDK maintains that intent across reconnects.

2. **Automatic Batching**: This design enables automatic batching of subscription requests. When you call `Subscribe()` on multiple subscriptions before or right after `Connect()`, the SDK batches them into a single protocol message:

```csharp
var sub1 = client.NewSubscription("channel1");
var sub2 = client.NewSubscription("channel2");
var sub3 = client.NewSubscription("channel3");

sub1.Subscribe();
sub2.Subscribe();
sub3.Subscribe();

client.Connect();
// All 3 subscriptions are batched together in one request!
```

3. **Consistency**: This pattern is consistent across all Centrifuge client SDKs (JavaScript, Go, Swift, Dart, etc.).

**If you need to wait for connection/subscription:**

```csharp
client.Connect();
await client.ReadyAsync();  // Wait until connected

subscription.Subscribe();
await subscription.ReadyAsync();  // Wait until subscribed
```

## Command Batching

The SDK automatically batches commands where possible for improved network efficiency. This is especially beneficial for HTTP-based transports.

- Commands are automatically batched with a 1ms delay window
- Batches are flushed immediately when they exceed 15KB to prevent oversized requests
- No configuration needed - batching is automatic

When you issue multiple commands without awaiting them immediately, they are queued and sent together in a single batch:

```csharp
// ❌ Sequential (no batching)
await subscription.PublishAsync(data1);
await subscription.PublishAsync(data2);
await subscription.PresenceAsync();
// Result: 3 separate network requests

// ✅ Batched (recommended)
var task1 = subscription.PublishAsync(data1);
var task2 = subscription.PublishAsync(data2);
var task3 = subscription.PresenceAsync();

await Task.WhenAll(task1, task2, task3);
// Result: 1 network request with all 3 commands!
```

Also, SDK automatically batches subscription requests under the hood (where possible) including re-subscriptions upon reconnect.

See the [Blazor example](examples/Centrifugal.Centrifuge.BlazorExample/Pages/Home.razor) for a complete demonstration of command batching in action.

## Unity Support

The library targets .NET Standard 2.1, which Unity supports since 2021.2.

**Verified:** Unity 6000.0.85f2 and 6000.6.10f1, Editor on macOS (Apple Silicon, Mono): WebSocket and HTTP-stream transports, covering connect, subscribe/publish, history, presence, reconnecting after a server disconnect, and `Dispose` on the main thread (see `make unity-test`). Player builds, IL2CPP, and other platforms (Windows, Linux, iOS, Android) are expected to work but are not tested yet. Reports are welcome.

**Platform Support:**
- ✅ **Editor, standalone and mobile builds**: WebSocket and HTTP-stream transports
- ❌ **WebGL**: Not supported. WebGL builds have no `System.Net` networking (no `ClientWebSocket`, no `HttpClient` streaming), and the SDK doesn't accept custom transports, so WebSocket plugins such as NativeWebSocket can't be used with it.

### Installing in Unity

Unity doesn't resolve NuGet dependencies by itself. Either install the `Centrifugal.Centrifuge` package with [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity), or copy the `netstandard2.1` DLLs of the package and its dependencies into `Assets/Plugins`:

- `Centrifugal.Centrifuge.dll`
- `Google.Protobuf.dll`
- `System.Threading.Channels.dll`
- `Microsoft.Extensions.Logging.Abstractions.dll`
- `System.Runtime.CompilerServices.Unsafe.dll`: Unity doesn't ship it, and without it Unity won't load `Google.Protobuf` (the Console shows `Unable to resolve reference 'System.Runtime.CompilerServices.Unsafe'`).

With NuGetForUnity, install `System.Runtime.CompilerServices.Unsafe` explicitly too. NuGetForUnity skips a dependency that a Unity package in the project already ships, and some packages do (older versions of `com.unity.collections`, for example). When such a package is updated, e.g. with a new Editor version, the DLL disappears from the project. The client then can't encode commands: it reports an `internal` error with a `FileNotFoundException` for that assembly and disconnects (see [Event Handlers and Callbacks](#event-handlers-and-callbacks)).

### Example

```csharp
using System.Collections.Concurrent;
using UnityEngine;
using Centrifugal.Centrifuge;

public class CentrifugeManager : MonoBehaviour
{
    private CentrifugeClient client;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();

    void Start()
    {
        client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

        client.Connected += (sender, e) => Debug.Log($"Connected: {e.ClientId}");

        var sub = client.NewSubscription("news");
        // Event handlers may run on a background thread: hand data over to the main thread.
        sub.Publication += (sender, e) => messages.Enqueue(System.Text.Encoding.UTF8.GetString(e.Data.Span));
        sub.Subscribe();

        client.Connect();
    }

    void Update()
    {
        // Main thread: safe to touch GameObjects, components and UI here.
        while (messages.TryDequeue(out var message))
        {
            Debug.Log($"Publication: {message}");
        }
    }

    void OnDestroy()
    {
        // Blocks until the disconnect completes; it doesn't need the main thread, so it can't deadlock.
        client?.Dispose();
    }
}
```

### Threading

Event handlers run on whichever thread delivers the event, usually a thread-pool thread and not Unity's main thread. Most Unity APIs (GameObjects, components, UI) may only be used on the main thread, so pass data to it as in the example, or post to the `SynchronizationContext` captured in `Start()`. `Debug.Log` is safe from any thread. Awaiting SDK methods from a MonoBehaviour's `async` method resumes on the main thread as usual.

### TLS (`wss://`)

On Unity, TLS goes through Unity's own TLS stack (UnityTLS), which checks the server certificate against the platform's trusted root certificates. Unity's `ClientWebSocket` doesn't call .NET certificate validation callbacks (`ServicePointManager.ServerCertificateValidationCallback` or `ClientWebSocketOptions.RemoteCertificateValidationCallback`), so a self-signed certificate can't be accepted from code. It fails with `UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED`. For local development, use `ws://` or a certificate your OS trusts.

### Troubleshooting

If the client doesn't connect in Unity, run the connection probe: copy [`unity/Project/Assets/Editor/CentrifugeProbe.cs`](unity/Project/Assets/Editor/CentrifugeProbe.cs) into your project's `Assets/Editor/`, set `Endpoint` at the top, and choose **Centrifuge → Run connection probe**. It checks the raw WebSocket, the TLS handshake, DNS and the thread pool separately, and copies a report to the clipboard that you can attach to an issue.

## Blazor Support

The library works in both Blazor Server and Blazor WebAssembly.

### Blazor Server

Works out of the box - uses standard .NET WebSocket client. No special configuration needed.

### Blazor WebAssembly

The SDK provides a simple DI-based configuration approach, similar to SignalR. Register Centrifuge services once in `Program.cs`:

```csharp
using Centrifugal.Centrifuge;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add Centrifuge client services - automatically initializes browser interop
builder.Services.AddCentrifugeClient();

await builder.Build().RunAsync();
```

Then create and use clients anywhere in your app:

```csharp
@page "/"
@inject IJSRuntime JS
@inject ILoggerFactory LoggerFactory
@implements IAsyncDisposable
@using System.Text

<h3>Messages</h3>
<ul>
    @foreach (var msg in messages)
    {
        <li>@msg</li>
    }
</ul>

@code {
    private CentrifugeClient? _client;
    private List<string> messages = new();

    protected override async Task OnInitializedAsync()
    {
        // Create client directly - no need to pass IJSRuntime
        _client = new CentrifugeClient(
            "ws://localhost:8000/connection/websocket",
            options: new CentrifugeClientOptions
            {
                Logger = LoggerFactory.CreateLogger<CentrifugeClient>()
            }
        );

        _client.Connected += (sender, e) =>
        {
            messages.Add($"Connected: {e.ClientId}");
            InvokeAsync(StateHasChanged);
        };

        var subscription = _client.NewSubscription("chat");

        subscription.Publication += (sender, e) =>
        {
            var data = Encoding.UTF8.GetString(e.Data.Span);
            messages.Add($"Received: {data}");
            InvokeAsync(StateHasChanged);
        };

        _client.Connect();
        subscription.Subscribe();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}
```

**How it works:**

- The SDK automatically uses browser-native transports when `AddCentrifugeClient()` is called
- WebSocket uses browser's native WebSocket via JS interop
- HTTP Stream uses browser's Fetch API with ReadableStream
- No need to pass `IJSRuntime` to constructors - it's configured globally

**Important**: The JavaScript interop modules are automatically included as static assets. Ensure your `index.html` has the standard Blazor script tag:

```html
<script src="_framework/blazor.webassembly.js"></script>
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/centrifugal/centrifuge-csharp.git
cd centrifuge-csharp

# Build the project
dotnet build

# Run tests
dotnet test

# Pack NuGet package
dotnet pack -c Release
```

## Publishing to NuGet

This library uses GitHub Actions for automated publishing. To release a new version:

1. Update the version in `src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj`
2. Create a git tag: `git tag v1.0.0`
3. Push the tag: `git push origin v1.0.0`
4. GitHub Actions will automatically build and publish to NuGet

### Manual Publishing

If you need to publish manually:

1. Create a NuGet account at [nuget.org](https://www.nuget.org/)
2. Get your API key from your account settings
3. Build and pack:
   ```bash
   dotnet pack -c Release
   ```
4. Publish:
   ```bash
   dotnet nuget push bin/Release/Centrifugal.Centrifuge.*.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
   ```

## Architecture

The SDK follows the [Centrifuge protocol specification](https://centrifugal.dev/docs/transports/protocol) and implements:

- **Client State Machine**: Manages connection lifecycle (Disconnected → Connecting → Connected)
- **Subscription State Machine**: Manages subscription lifecycle (Unsubscribed → Subscribing → Subscribed)
- **Transport Layer**: Abstraction over WebSocket and HTTP-streaming
- **Exponential Backoff**: Smart reconnection with full jitter to avoid thundering herd
- **Varint Encoding**: Efficient Protobuf message framing
- **Event Loop**: every state change runs as a step of one loop per client, following [centrifuge-js](https://github.com/centrifugal/centrifuge-js) and the SDK specification; events are raised in the order of the steps

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Links

- [Centrifugo Documentation](https://centrifugal.dev/)
- [Client SDK Specification](https://centrifugal.dev/docs/transports/client_api)
- [Centrifuge Protocol](https://centrifugal.dev/docs/transports/protocol)
- [GitHub Repository](https://github.com/centrifugal/centrifuge-csharp)
- [NuGet Package](https://www.nuget.org/packages/Centrifugal.Centrifuge/)

## Support

- GitHub Issues: [Report bugs or request features](https://github.com/centrifugal/centrifuge-csharp/issues)
- [Community rooms](https://centrifugal.dev/docs/getting-started/community)
