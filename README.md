# Centrifuge C# SDK

❗❗❗ This SDK is a work in progress, we do not recommend using it in the production ❗❗❗

[![NuGet](https://img.shields.io/nuget/v/Centrifugal.Centrifuge.svg)](https://www.nuget.org/packages/Centrifugal.Centrifuge/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

C# client SDK for [Centrifugo](https://github.com/centrifugal/centrifugo) and [Centrifuge](https://github.com/centrifugal/centrifuge) real-time messaging servers.

## Features

- ✅ Multi-platform support
- ✅ WebSocket and HTTP-streaming transports with automatic fallback
- ✅ Browser-native transports for Blazor WebAssembly (using JS interop)
- ✅ Protobuf binary protocol for efficient communication
- ✅ Automatic command batching for improved network efficiency
- ✅ Automatic reconnection with exponential backoff and full jitter
- ✅ Channel subscriptions with recovery and Fossil delta compression support
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
| **WebSocket** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ⚠️ Plugin Required |
| **HTTP Stream** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ❌ Not Supported |

**\*Blazor WebAssembly**: Requires `builder.Services.AddCentrifugeClient()` in `Program.cs`. The SDK automatically uses browser-native implementations via JavaScript interop for both WebSocket and HTTP Stream transports.

**Unity WebGL**: Requires a third-party WebSocket plugin. HTTP Stream is not supported in WebGL.

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

### Subscriptions

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

    // Timeouts
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

The library works with Unity 2021.2+ (requires .NET Standard 2.1 support).

**Platform Support:**
- ✅ **Standalone builds** (Windows, macOS, Linux): Works out of the box
- ✅ **Mobile** (iOS, Android): Works out of the box
- ⚠️ **WebGL**: Requires a WebSocket plugin that provides `System.Net.WebSockets` support, such as:
  - [NativeWebSocket](https://github.com/endel/NativeWebSocket)
  - [WebSocketSharp](https://github.com/sta/websocket-sharp) with Unity WebGL wrapper

Example for Unity (standalone/mobile):

```csharp
using UnityEngine;
using Centrifugal.Centrifuge;
using System.Threading.Tasks;

public class CentrifugeManager : MonoBehaviour
{
    private CentrifugeClient client;

    async void Start()
    {
        client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

        client.Connected += (sender, e) =>
        {
            Debug.Log($"Connected: {e.ClientId}");
        };

        client.Connect();
    }

    void OnDestroy()
    {
        if (client != null)
        {
            // Dispose() blocks until disconnect completes
            client.Dispose();
        }
    }
}
```

> **Note**: Unity WebGL builds run in the browser but don't have built-in `System.Net.WebSockets` support. You'll need a third-party WebSocket library that works in Unity WebGL.

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
- **Event-Driven Architecture**: Thread-safe event handling for all state changes

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
