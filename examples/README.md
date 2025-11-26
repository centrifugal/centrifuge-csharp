# Centrifuge C# SDK Examples

This directory contains example programs demonstrating how to use the Centrifuge C# SDK in different environments:

1. **Console Example** - .NET Console Application
2. **Blazor Example** - Blazor WebAssembly Application (browser-based)

## Prerequisites

1. **Install .NET SDK** (.NET 8.0 or later recommended)
   - Download from: https://dotnet.microsoft.com/download

2. **Run Centrifugo Server**

The easiest way is using Docker (note, we are using client insecure mode here and allowed origins `*`, it's only for the example purposes, avoid this in production app unti you know what you do):

```bash
docker pull centrifugo/centrifugo:v6
docker run -p 8000:8000 \
-e CENTRIFUGO_HTTP_STREAM_ENABLED="true" \
-e CENTRIFUGO_CLIENT_INSECURE="true" \
-e CENTRIFUGO_CLIENT_ALLOWED_ORIGINS="*" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_DELTA_PUBLISH="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_PRESENCE="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_JOIN_LEAVE="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_FORCE_PUSH_JOIN_LEAVE="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_HISTORY_SIZE="100" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_HISTORY_TTL="300s" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_FORCE_RECOVERY="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_ALLOW_PUBLISH_FOR_SUBSCRIBER="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_ALLOW_PRESENCE_FOR_SUBSCRIBER="true" \
-e CENTRIFUGO_CHANNEL_WITHOUT_NAMESPACE_ALLOW_HISTORY_FOR_SUBSCRIBER="true" \
-e CENTRIFUGO_CLIENT_SUBSCRIBE_TO_USER_PERSONAL_CHANNEL_ENABLED="true" \
-e CENTRIFUGO_LOG_LEVEL="trace" \
centrifugo/centrifugo:v6 centrifugo
```

## Running the Examples

### Console Example

**Using Make (recommended):**

From the repository root:

```bash
make run-example
```

**Using dotnet CLI:**

From the repository root:

```bash
dotnet run --project examples/Centrifugal.Centrifuge.ConsoleExample/Centrifugal.Centrifuge.ConsoleExample.csproj
```

Or from the examples directory:

```bash
cd examples/Centrifugal.Centrifuge.ConsoleExample
dotnet run
```

### Blazor WebAssembly Example

**Using Make (recommended):**

From the repository root:

```bash
make run-blazor-example
```

This will:
- Start the Blazor dev server on port 5050
- Automatically open your browser at `http://localhost:5050`
- Display connection status with color-coded indicators
- Show live event log with color-coded categories

**Using dotnet CLI:**

From the repository root:

```bash
cd examples/Centrifugal.Centrifuge.BlazorExample
dotnet run --urls "http://localhost:5050" --launch-profile "http"
```

**What to see:**
- Visual status bar with real-time connection and subscription states
- Live event log with color-coded categories (client, subscription, errors, etc.)
- Clear button to reset the event log
- All the same features as the console example, running in the browser with a nice UI!

## What the Example Demonstrates

The example program shows:

- **Client Connection**: Connecting to Centrifugo server with event handlers
- **State Management**: Monitoring client and subscription state changes
- **Subscriptions**: Subscribing to a channel (`chat:index`)
- **Publishing**: Sending messages to a channel
- **Real-time Messages**: Receiving publications from the server
- **Presence**: Getting information about clients in a channel
- **History**: Fetching message history
- **RPC**: Making remote procedure calls to the server
- **Join/Leave Events**: Tracking when clients join or leave channels
- **Error Handling**: Handling various error scenarios

## Customization

You can modify the example to:

1. **Change the server URL**:
   ```csharp
   var client = new CentrifugeClient("ws://your-server:8000/connection/websocket", options);
   ```

2. **Add authentication**:
   ```csharp
   var options = new CentrifugeClientOptions
   {
       Token = "your-jwt-token",
       GetToken = async () => await FetchTokenFromYourBackend()
   };
   ```

3. **Subscribe to different channels**:
   ```csharp
   var subscription = client.NewSubscription("your-channel-name");
   ```

## Expected Output

When you run the example with Centrifugo server running, you should see output similar to:

```
Centrifuge C# SDK Example
========================

Connecting to Centrifugo...
[Client] Connecting: 0 - connect called
[Client] State: Disconnected -> Connecting
[Client] Connected!
  Client ID: 8d3f5e2c-...
  Transport: websocket
[Client] State: Connecting -> Connected

Subscribing to channel 'chat:index'...
[Subscription] Subscribing: 0 - subscribe called
[Subscription] State: Unsubscribed -> Subscribing
[Subscription] Subscribed!
[Subscription] State: Subscribing -> Subscribed

Publishing message...
[Subscription] Publication received:
  Channel: chat:index
  Data: {"text":"Hello from C# SDK!"}
  From: 8d3f5e2c-... (user: )

Fetching presence...
Clients in channel: 1
  - 8d3f5e2c-...: user=
Presence stats: 1 clients, 1 users

...
```

## Next Steps

- Check out the [main README](../../README.md) for more SDK features
- Read the [Centrifugo documentation](https://centrifugal.dev/)
- Explore different subscription options and client configurations
- Build your own real-time application!
