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
        /// Gets or sets the initial connection token (JWT).
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
        public byte[]? Data { get; set; }

        /// <summary>
        /// Gets or sets the client name (not unique per connection, identifies where client connected from).
        /// Default is "csharp".
        /// </summary>
        public string Name { get; set; } = "csharp";

        /// <summary>
        /// Gets or sets the client version.
        /// Default is the assembly version.
        /// </summary>
        public string Version { get; set; } = typeof(CentrifugeClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";

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

        /// <summary>
        /// Gets or sets the custom headers to emulate (sent with first protocol message).
        /// Requires Centrifugo v6+.
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }

        /// <summary>
        /// Gets or sets the emulation endpoint for SSE and HTTP Stream transports.
        /// This endpoint is used to send commands when using unidirectional transports.
        /// If not set, will be auto-constructed from the transport endpoint by replacing the last path segment with "emulation".
        /// </summary>
        public string? EmulationEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the command batching delay in milliseconds.
        /// Commands sent within this delay window will be batched together for better network efficiency.
        /// Set to 0 to disable batching. Default is 1ms.
        /// </summary>
        public int CommandBatchDelayMs { get; set; } = 1;

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

            if (Timeout <= TimeSpan.Zero)
            {
                throw new CentrifugeConfigurationException("Timeout must be positive");
            }

            if (MaxServerPingDelay <= TimeSpan.Zero)
            {
                throw new CentrifugeConfigurationException("MaxServerPingDelay must be positive");
            }

            if (CommandBatchDelayMs < 0)
            {
                throw new CentrifugeConfigurationException("CommandBatchDelayMs cannot be negative");
            }
        }
    }
}
