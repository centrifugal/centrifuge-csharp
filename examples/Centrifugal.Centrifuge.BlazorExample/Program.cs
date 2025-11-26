using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Centrifugal.Centrifuge.BlazorExample;
using Centrifugal.Centrifuge;
using Microsoft.Extensions.Logging;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Configure logging for Centrifuge client only (logs will appear in browser console)
builder.Logging.AddFilter("Centrifugal.Centrifuge", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);  // Suppress Microsoft framework logs

// Add Centrifuge client services - automatically initializes browser interop
builder.Services.AddCentrifugeClient();

await builder.Build().RunAsync();
