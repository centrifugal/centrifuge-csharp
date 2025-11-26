#if NET6_0_OR_GREATER
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Extension methods for configuring Centrifuge services in DI container.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds Centrifuge client services to the service collection and initializes browser interop for Blazor WebAssembly.
        /// This automatically configures IJSRuntime for all Centrifuge clients created in the application.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddCentrifugeClient(this IServiceCollection services)
        {
            // Build a temporary service provider to resolve IJSRuntime and initialize immediately
            using (var serviceProvider = services.BuildServiceProvider())
            {
                var jsRuntime = serviceProvider.GetService<IJSRuntime>();
                if (jsRuntime != null)
                {
                    CentrifugeClient.InitializeBrowserInterop(jsRuntime);
                }
            }
            return services;
        }
    }
}
#endif
