using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Centrifugal.Centrifuge.BlazorExample;
using Centrifugal.Centrifuge;
using Microsoft.JSInterop;

// Configure transport type - change this to switch between transports
const bool UseHttpStreaming = true; // Set to true to use HTTP streaming instead of WebSocket

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register CentrifugeClient as scoped service
builder.Services.AddScoped(sp =>
{
    var jsRuntime = sp.GetRequiredService<IJSRuntime>();

    CentrifugeClient client;

    if (UseHttpStreaming)
    {
        // Use HTTP streaming transport
        client = new CentrifugeClient(
            new[]
            {
                new CentrifugeTransportEndpoint(
                    CentrifugeTransportType.HttpStream,
                    "http://localhost:8000/connection/http_stream"
                )
            },
            jsRuntime,
            new CentrifugeClientOptions
            {
                Debug = true
            }
        );
    }
    else
    {
        // Use WebSocket transport (default)
        client = new CentrifugeClient(
            "ws://localhost:8000/connection/websocket",
            jsRuntime,
            new CentrifugeClientOptions
            {
                Debug = true
            }
        );
    }

    // Alternative: Use automatic transport fallback
    // Tries WebSocket first, falls back to HTTP streaming if WebSocket fails
    /*
    client = new CentrifugeClient(
        new[]
        {
            new CentrifugeTransportEndpoint(
                CentrifugeTransportType.WebSocket,
                "ws://localhost:8000/connection/websocket"
            ),
            new CentrifugeTransportEndpoint(
                CentrifugeTransportType.HttpStream,
                "http://localhost:8000/connection/http_stream"
            )
        },
        jsRuntime,
        new CentrifugeClientOptions
        {
            Debug = true
        }
    );
    */

    return client;
});

await builder.Build().RunAsync();
