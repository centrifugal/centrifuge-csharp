# Centrifuge C# SDK

[![NuGet](https://img.shields.io/nuget/v/Centrifugal.Centrifuge.svg)](https://www.nuget.org/packages/Centrifugal.Centrifuge/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

C# client SDK for [Centrifugo](https://github.com/centrifugal/centrifugo) and [Centrifuge](https://github.com/centrifugal/centrifuge) real-time messaging servers.

## Features

- ✅ WebSocket, SSE, and HTTP-streaming transports with automatic fallback
- ✅ Protobuf binary protocol for efficient communication
- ✅ Automatic reconnection with exponential backoff and full jitter
- ✅ Channel subscriptions with recovery and positioning
- ✅ JWT authentication with automatic token refresh
- ✅ Publication, join/leave events
- ✅ RPC calls
- ✅ Presence and presence stats
- ✅ History API
- ✅ Async/await throughout
- ✅ Thread-safe
- ✅ Comprehensive event system
- ✅ Multi-platform support

## Platform Support

The library targets multiple frameworks for maximum compatibility:

- .NET 9.0+
- .NET 8.0
- .NET 7.0
- .NET 6.0
- .NET Standard 2.1
- .NET Standard 2.0 (.NET Framework 4.6.1+, .NET Core 2.0+)

This means it works on:

- ✅ Server-side: .NET 5/6/7/8/9, .NET Core, .NET Framework
- ✅ Desktop: Windows, macOS, Linux
- ✅ Mobile: Xamarin.iOS, Xamarin.Android, .NET MAUI
- ✅ Web: Blazor WebAssembly, Blazor Server
- ✅ Unity: All platforms (including WebGL with proper WebSocket plugin)
- ✅ UWP: Universal Windows Platform

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

// Create client instance
var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

// Setup event handlers
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

// Connect to server
await client.ConnectAsync();

// ... use the client ...

// Disconnect when done
await client.DisconnectAsync();
client.Dispose();
```

### Subscriptions

```csharp
// Create subscription
var subscription = client.NewSubscription("chat");

// Setup subscription event handlers
subscription.Publication += (sender, e) =>
{
    var data = Encoding.UTF8.GetString(e.Data);
    Console.WriteLine($"Message from {e.Channel}: {data}");
};

subscription.Subscribed += (sender, e) =>
{
    Console.WriteLine($"Subscribed to channel, recovered: {e.Recovered}");
};

subscription.Unsubscribed += (sender, e) =>
{
    Console.WriteLine($"Unsubscribed: {e.Code} - {e.Reason}");
};

// Subscribe to channel
await subscription.SubscribeAsync();

// Publish to channel
var message = Encoding.UTF8.GetBytes("Hello, world!");
await subscription.PublishAsync(message);

// Unsubscribe when done
await subscription.UnsubscribeAsync();
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
var subscriptionOptions = new SubscriptionOptions
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
await subscription.SubscribeAsync();
```

### History API

```csharp
// Get last 10 messages
var history = await subscription.HistoryAsync(new HistoryOptions
{
    Limit = 10
});

foreach (var pub in history.Publications)
{
    var data = Encoding.UTF8.GetString(pub.Data);
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
var response = Encoding.UTF8.GetString(result.Data);
Console.WriteLine($"RPC result: {response}");
```

### Advanced Configuration

```csharp
var options = new CentrifugeClientOptions
{
    // Connection token and refresh
    Token = "your-jwt-token",
    GetToken = async () => await FetchTokenAsync(),

    // Connection data
    Data = Encoding.UTF8.GetBytes("{\"custom\":\"data\"}"),

    // Client identification
    Name = "my-app",
    Version = "1.0.0",

    // Reconnection settings
    MinReconnectDelay = TimeSpan.FromMilliseconds(500),
    MaxReconnectDelay = TimeSpan.FromSeconds(20),

    // Timeouts
    Timeout = TimeSpan.FromSeconds(5),
    MaxServerPingDelay = TimeSpan.FromSeconds(10),

    // Debugging
    Debug = true,

    // Custom headers (requires Centrifugo v6+)
    Headers = new Dictionary<string, string>
    {
        ["X-Custom-Header"] = "value"
    }
};

var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
```

### Recovery and Positioning

```csharp
var options = new SubscriptionOptions
{
    // Enable recovery (if supported by server)
    Recoverable = true,

    // Enable positioning
    Positioned = true,

    // Start from known position
    Since = new StreamPosition(offset: 100, epoch: "epoch-id")
};

var subscription = client.NewSubscription("chat", options);

subscription.Subscribed += (sender, e) =>
{
    if (e.WasRecovering)
    {
        Console.WriteLine($"Recovery attempted, recovered: {e.Recovered}");
    }
};

await subscription.SubscribeAsync();
```

## Error Handling

```csharp
try
{
    await client.RpcAsync("method", data);
}
catch (UnauthorizedException)
{
    Console.WriteLine("Authentication failed");
}
catch (TimeoutException)
{
    Console.WriteLine("Operation timed out");
}
catch (CentrifugeException ex)
{
    Console.WriteLine($"Centrifuge error: {ex.Code} - {ex.Message}");
    Console.WriteLine($"Temporary: {ex.Temporary}");
}
```

## Unity Support

The library works with Unity, but you'll need:

1. Use .NET Standard 2.1 or .NET 4.x scripting runtime
2. For WebGL, use a WebSocket plugin that provides `System.Net.WebSockets` support (like [NativeWebSocket](https://github.com/endel/NativeWebSocket))

Example for Unity:

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

        await client.ConnectAsync();
    }

    async void OnDestroy()
    {
        if (client != null)
        {
            await client.DisconnectAsync();
            client.Dispose();
        }
    }
}
```

## Blazor Support

Works seamlessly in both Blazor Server and Blazor WebAssembly:

```csharp
@inject CentrifugeClient Client

@code {
    protected override async Task OnInitializedAsync()
    {
        Client.Publication += async (sender, e) =>
        {
            // Update UI on message
            await InvokeAsync(StateHasChanged);
        };

        await Client.ConnectAsync();
    }
}
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

1. Update the version in `src/Centrifuge/Centrifuge.csproj`
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
- **Transport Layer**: Abstraction over WebSocket, SSE, and HTTP-streaming
- **Exponential Backoff**: Smart reconnection with full jitter to avoid thundering herd
- **Varint Encoding**: Efficient Protobuf message framing
- **Event-Driven Architecture**: Thread-safe event handling for all state changes

## Thread Safety

All public methods are thread-safe. The library uses:

- `SemaphoreSlim` for state synchronization
- `ConcurrentDictionary` for subscription and callback registries
- Task-based asynchronous patterns throughout

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
- [Client SDK Specification](https://centrifugal.dev/docs/transports/client_sdk)
- [Centrifuge Protocol](https://centrifugal.dev/docs/transports/protocol)
- [GitHub Repository](https://github.com/centrifugal/centrifuge-csharp)
- [NuGet Package](https://www.nuget.org/packages/Centrifugal.Centrifuge/)

## Support

- GitHub Issues: [Report bugs or request features](https://github.com/centrifugal/centrifuge-csharp/issues)
- Community Forum: [Centrifugal Forum](https://centrifugal.dev/docs/getting-started/community)
- Telegram: [@centrifugal](https://t.me/joinchat/ABFVWBE0AhkyyhREoaboXQ)
