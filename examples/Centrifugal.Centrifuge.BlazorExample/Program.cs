using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Centrifugal.Centrifuge.BlazorExample;
using Centrifugal.Centrifuge;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register CentrifugeClient as scoped service
builder.Services.AddScoped(sp =>
{
    var jsRuntime = sp.GetRequiredService<IJSRuntime>();
    var client = new CentrifugeClient(
        "ws://localhost:8000/connection/websocket",
        jsRuntime,
        new CentrifugeClientOptions
        {
            Debug = true
        }
    );
    return client;
});

await builder.Build().RunAsync();
