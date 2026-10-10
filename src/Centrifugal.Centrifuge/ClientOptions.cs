using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Options for configuring the Centrifuge client.
    /// </summary>
    public class CentrifugeClientOptions
    {
        /// <summary>
        /// Gets or sets the initial connection token (JWT). For proper JWT expiration handling
        /// GetToken must be also set.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/refresh connection token.
        /// This will only be called when a new token is needed, not on every reconnect.
        /// Throw <see cref="CentrifugeUnauthorizedException"/> to stop token refresh attempts.
        /// </summary>
        public Func<Task<string>>? GetToken { get; set; }

        /// <summary>
        /// Gets or sets the connection data to send with connect command.
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; set; }

        /// <summary>
        /// Gets or sets the client name (not unique per connection, identifies where client connected from).
        /// Default is "csharp".
        /// </summary>
        public string Name { get; set; } = "csharp";

        /// <summary>
        /// Gets or sets the client version.
        /// By default not set.
        /// </summary>
        public string Version { get; set; } = "";

        /// <summary>
        /// Gets or sets the minimum delay between reconnect attempts.
        /// Default is 200ms.
        /// </summary>
        public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Gets or sets the maximum delay between reconnect attempts.
        /// Default is 20 seconds.
        /// </summary>
        public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the timeout for operations.
        /// Default is 5 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the maximum delay of server pings to detect broken connection.
        /// Default is 10 seconds.
        /// </summary>
        public TimeSpan MaxServerPingDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the logger for diagnostic output.
        /// When set, debug-level logs will be written for connection lifecycle, transport operations, and protocol messages.
        /// </summary>
        public ILogger? Logger { get; set; }

#if NET6_0_OR_GREATER
        /// <summary>
        /// Gets or sets the IJSRuntime for Blazor WebAssembly support.
        /// If not set, will use the global instance configured via CentrifugeClient.InitializeBrowserInterop().
        /// Only needed when running in browser environments (Blazor WASM).
        /// </summary>
        public Microsoft.JSInterop.IJSRuntime? JSRuntime { get; set; }
#endif

        /// <summary>
        /// Gets or sets the custom headers to emulate (sent with first protocol message).
        /// Requires Centrifugo v6+.
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }

        /// <summary>
        /// Gets or sets the emulation endpoint for SSE and HTTP Stream transports.
        /// This endpoint is used to send commands when using unidirectional transports.
        /// Defaults to /emulation at the root of the transport endpoint's host, which is where Centrifugo
        /// serves it. Set it explicitly if the server sits behind a path prefix. For an HTTP streaming
        /// endpoint the value has to be an absolute http/https URL; in the browser a page-relative URL
        /// works too.
        /// </summary>
        public string? EmulationEndpoint { get; set; }

        /// <summary>Shallow copy kept by the client, so the caller can't change its options afterwards.</summary>
        internal CentrifugeClientOptions Clone() => (CentrifugeClientOptions)MemberwiseClone();

        /// <summary>
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (MinReconnectDelay < TimeSpan.Zero)
            {
                throw new CentrifugeConfigurationException("MinReconnectDelay cannot be negative");
            }

            if (MaxReconnectDelay < MinReconnectDelay)
            {
                throw new CentrifugeConfigurationException("MaxReconnectDelay must be >= MinReconnectDelay");
            }

            if (MaxReconnectDelay > Utilities.MaxTimerInterval)
            {
                throw new CentrifugeConfigurationException("MaxReconnectDelay must not exceed Int32.MaxValue milliseconds");
            }

            if (Timeout <= TimeSpan.Zero || Timeout > Utilities.MaxTimerInterval)
            {
                throw new CentrifugeConfigurationException("Timeout must be positive and not exceed Int32.MaxValue milliseconds");
            }

            if (MaxServerPingDelay <= TimeSpan.Zero)
            {
                throw new CentrifugeConfigurationException("MaxServerPingDelay must be positive");
            }

            if (Name == null || Version == null || (Headers != null && Headers.ContainsValue(null!)))
            {
                throw new CentrifugeConfigurationException("Name, Version and header values must not be null");
            }
        }
    }
}
