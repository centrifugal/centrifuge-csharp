using System;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Represents a transport endpoint configuration for multi-transport fallback.
    /// </summary>
    public class CentrifugeTransportEndpoint
    {
        /// <summary>
        /// Gets or sets the transport type.
        /// </summary>
        public CentrifugeTransportType Transport { get; set; }

        /// <summary>
        /// Gets or sets the endpoint URL for this transport.
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeTransportEndpoint"/> class.
        /// </summary>
        public CentrifugeTransportEndpoint(CentrifugeTransportType transport, string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));
            }

            Transport = transport;
            Endpoint = endpoint;
        }
    }
}
