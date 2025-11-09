using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Centrifugal.Centrifuge.Protocol;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a transport endpoint configuration for multi-transport fallback.
    /// </summary>
    public class TransportEndpoint
    {
        /// <summary>
        /// Gets or sets the transport type.
        /// </summary>
        public TransportType Transport { get; set; }

        /// <summary>
        /// Gets or sets the endpoint URL for this transport.
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransportEndpoint"/> class.
        /// </summary>
        public TransportEndpoint(TransportType transport, string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            Transport = transport;
            Endpoint = endpoint;
        }
    }

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
        /// Throw <see cref="UnauthorizedException"/> to stop token refresh attempts.
        /// </summary>
        public Func<Task<string>>? GetToken { get; set; }

        /// <summary>
        /// Gets or sets the connection data to send with connect command.
        /// </summary>
        public byte[]? Data { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/renew connection data (called upon reconnects).
        /// </summary>
        public Func<Task<byte[]>>? GetData { get; set; }

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
        /// Default is 500ms.
        /// </summary>
        public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(500);

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
        /// Gets or sets whether to enable debug logging.
        /// </summary>
        public bool Debug { get; set; }

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
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (MinReconnectDelay < TimeSpan.Zero)
            {
                throw new ConfigurationException("MinReconnectDelay cannot be negative");
            }

            if (MaxReconnectDelay < MinReconnectDelay)
            {
                throw new ConfigurationException("MaxReconnectDelay must be >= MinReconnectDelay");
            }

            if (Timeout <= TimeSpan.Zero)
            {
                throw new ConfigurationException("Timeout must be positive");
            }

            if (MaxServerPingDelay <= TimeSpan.Zero)
            {
                throw new ConfigurationException("MaxServerPingDelay must be positive");
            }
        }
    }

    /// <summary>
    /// Options for configuring a subscription.
    /// </summary>
    public class SubscriptionOptions
    {
        /// <summary>
        /// Gets or sets the initial subscription token (JWT).
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/refresh subscription token.
        /// Throw <see cref="UnauthorizedException"/> to stop token refresh attempts.
        /// </summary>
        public Func<string, Task<string>>? GetToken { get; set; }

        /// <summary>
        /// Gets or sets the subscription data (attached to every subscribe/resubscribe request).
        /// </summary>
        public byte[]? Data { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/renew subscription data (called upon resubscribes).
        /// </summary>
        public Func<Task<byte[]>>? GetData { get; set; }

        /// <summary>
        /// Gets or sets the minimum delay between resubscribe attempts.
        /// Default is 500ms.
        /// </summary>
        public TimeSpan MinResubscribeDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets the maximum delay between resubscribe attempts.
        /// Default is 20 seconds.
        /// </summary>
        public TimeSpan MaxResubscribeDelay { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the stream position to start subscription from (attempt recovery on first subscribe).
        /// </summary>
        public StreamPosition? Since { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to make subscription positioned (if not forced by server).
        /// </summary>
        public bool Positioned { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to make subscription recoverable (if not forced by server).
        /// </summary>
        public bool Recoverable { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to push Join/Leave messages (if not forced by server).
        /// </summary>
        public bool JoinLeave { get; set; }

        /// <summary>
        /// Gets or sets the server-side publication filter based on publication tags.
        /// Cannot be used together with delta compression.
        /// </summary>
        public FilterNode? TagsFilter { get; set; }

        /// <summary>
        /// Gets or sets the delta format to use for bandwidth optimization.
        /// Currently only "fossil" is supported.
        /// Cannot be used together with tags filtering.
        /// </summary>
        public string? Delta { get; set; }

        /// <summary>
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (MinResubscribeDelay < TimeSpan.Zero)
            {
                throw new ConfigurationException("MinResubscribeDelay cannot be negative");
            }

            if (MaxResubscribeDelay < MinResubscribeDelay)
            {
                throw new ConfigurationException("MaxResubscribeDelay must be >= MinResubscribeDelay");
            }

            if (!string.IsNullOrEmpty(Delta) && Delta != "fossil")
            {
                throw new ConfigurationException("Unsupported delta format. Only 'fossil' is supported.");
            }

            if (!string.IsNullOrEmpty(Delta) && TagsFilter != null)
            {
                throw new ConfigurationException("Cannot use delta and TagsFilter together");
            }
        }
    }

    /// <summary>
    /// Options for history requests.
    /// </summary>
    public class HistoryOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of publications to return.
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets the stream position to get publications since.
        /// </summary>
        public StreamPosition? Since { get; set; }

        /// <summary>
        /// Gets or sets whether to return publications in reverse order (newest first).
        /// </summary>
        public bool Reverse { get; set; }
    }

    /// <summary>
    /// Result of history request.
    /// </summary>
    public class HistoryResult
    {
        /// <summary>
        /// Gets the publications.
        /// </summary>
        public PublicationEventArgs[] Publications { get; }

        /// <summary>
        /// Gets the stream epoch.
        /// </summary>
        public string Epoch { get; }

        /// <summary>
        /// Gets the stream offset.
        /// </summary>
        public ulong Offset { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HistoryResult"/> class.
        /// </summary>
        public HistoryResult(PublicationEventArgs[] publications, string epoch, ulong offset)
        {
            Publications = publications ?? Array.Empty<PublicationEventArgs>();
            Epoch = epoch ?? string.Empty;
            Offset = offset;
        }
    }

    /// <summary>
    /// Result of presence request.
    /// </summary>
    public class PresenceResult
    {
        /// <summary>
        /// Gets the map of client IDs to client information.
        /// </summary>
        public IReadOnlyDictionary<string, ClientInfo> Clients { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PresenceResult"/> class.
        /// </summary>
        public PresenceResult(IReadOnlyDictionary<string, ClientInfo> clients)
        {
            Clients = clients ?? new Dictionary<string, ClientInfo>();
        }
    }

    /// <summary>
    /// Result of presence stats request.
    /// </summary>
    public class PresenceStatsResult
    {
        /// <summary>
        /// Gets the number of clients in the channel.
        /// </summary>
        public uint NumClients { get; }

        /// <summary>
        /// Gets the number of unique users in the channel.
        /// </summary>
        public uint NumUsers { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PresenceStatsResult"/> class.
        /// </summary>
        public PresenceStatsResult(uint numClients, uint numUsers)
        {
            NumClients = numClients;
            NumUsers = numUsers;
        }
    }

    /// <summary>
    /// Result of RPC request.
    /// </summary>
    public class RpcResult
    {
        /// <summary>
        /// Gets the RPC result data.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcResult"/> class.
        /// </summary>
        public RpcResult(byte[] data)
        {
            Data = data ?? Array.Empty<byte>();
        }
    }
}
