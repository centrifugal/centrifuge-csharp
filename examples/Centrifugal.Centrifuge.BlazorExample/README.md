# Centrifuge Blazor WebAssembly Example

This is a Blazor WebAssembly example demonstrating the Centrifuge C# SDK in a browser environment.

## Features

- WebSocket connection using browser-native WebSocket via JavaScript interop
- Real-time subscriptions to Centrifuge channels
- Publishing messages
- Presence tracking
- History fetching
- RPC calls
- Live event log displayed on screen with color-coded categories
- Visual status indicators for connection and subscription state
- Clear button to reset the event log

## Prerequisites

- .NET 9.0 SDK
- Centrifugo server running on `ws://localhost:8000` (or update the endpoint in `Program.cs`)

## Running the Example

From the repository root:

```bash
make run-blazor-example
```

Or manually:

```bash
cd examples/Centrifugal.Centrifuge.BlazorExample
dotnet run --urls "http://localhost:5050" --launch-profile "http"
```

The browser will automatically open at `http://localhost:5050`.

## What to Expect

1. **Status Bar**: Shows real-time connection and subscription state with color-coded indicators
   - Green: Connected/Subscribed
   - Yellow: Connecting/Subscribing
   - Red: Disconnected/Unsubscribed

2. **Events Log**: Live feed of all SDK events with color-coded categories
   - Blue: Client events
   - Cyan: Subscription events
   - Red: Errors
   - Green: Success messages
   - Orange: Informational messages

3. The example will automatically:
   - Connect to the Centrifugo server
   - Subscribe to the "chat" channel
   - Publish a test message
   - Fetch presence information
   - Fetch message history (if available)
   - Attempt an RPC call (if configured on server)
   - Remain connected to receive incoming messages

4. **Clear Button**: Click to reset the events log

## How It Works

The example uses the Centrifuge SDK's browser-native transports:

- **WebSocket**: Uses browser's native WebSocket via JavaScript interop (`BrowserWebSocketTransport`)
- **HTTP Stream**: Uses browser's Fetch API with ReadableStream (`BrowserHttpStreamTransport`)

The SDK uses a simple DI-based configuration approach:

1. Call `builder.Services.AddCentrifugeClient()` in `Program.cs`
2. Create `CentrifugeClient` instances anywhere in your app without passing `IJSRuntime`

This follows standard .NET conventions, just like SignalR's `AddSignalR()` - configure once, use everywhere!

## Code Structure

- **Program.cs**: Registers Centrifuge with `AddCentrifugeClient()`
- **Pages/Home.razor**: Creates client directly and logs all events
- All SDK events are logged both to browser console and displayed in the UI

## Troubleshooting

**Connection Failed:**
- Ensure Centrifugo is running on `ws://localhost:8000`
- Check browser console for detailed error messages
- Verify CORS is configured on the server if needed

**Module Loading Errors:**
- The JavaScript modules are automatically loaded from `_content/Centrifugal.Centrifuge/`
- Ensure the SDK package is properly referenced

**Build Errors:**
- Run `dotnet restore` to ensure all NuGet packages are installed
- Check that you have .NET 9.0 SDK installed
