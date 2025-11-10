using System;

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
}
