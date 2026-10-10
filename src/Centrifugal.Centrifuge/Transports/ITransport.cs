using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge.Transports
{
    /// <summary>
    /// Transport for communicating with Centrifugo server.
    /// </summary>
    internal interface ITransport : IDisposable
    {
        /// <summary>
        /// Gets the transport type.
        /// </summary>
        CentrifugeTransportType Type { get; }

        /// <summary>
        /// Gets the transport name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets whether this transport uses emulation mode.
        /// Emulation mode is used for unidirectional transports (HTTP Stream at this point)
        /// where sends go through a separate emulation endpoint.
        /// </summary>
        bool UsesEmulation { get; }

        /// <summary>
        /// Event raised when transport is opened and ready to send/receive.
        /// </summary>
        event EventHandler? Opened;

        /// <summary>
        /// Event raised once per received frame with its messages, in order. The client
        /// handles a frame as one step on its event loop, so no reply timeout can fire
        /// between messages of the same frame. The transport may reuse the list once the
        /// handler returns, so copy it to keep it.
        /// </summary>
        event EventHandler<IReadOnlyList<byte[]>>? MessageReceived;

        /// <summary>
        /// Event raised when transport is closed.
        /// </summary>
        event EventHandler<TransportClosedEventArgs>? Closed;

        /// <summary>
        /// Event raised when an error occurs.
        /// </summary>
        event EventHandler<Exception>? Error;

        /// <summary>
        /// Opens the transport connection.
        /// </summary>
        /// <param name="cancellationToken">The client's open deadline; when it is cancelled the
        /// transport stops opening.</param>
        /// <param name="initialData">Initial data to send with connection (for emulation transports).</param>
        Task OpenAsync(CancellationToken cancellationToken = default, byte[]? initialData = null);

        /// <summary>
        /// Sends data through the transport.
        /// Data should already be properly formatted (varint-delimited for protocol messages).
        /// Closed always follows a write that fails or is cancelled. Cancellation closes the transport
        /// right away. A failure makes later writes fail and closes the transport shortly after, unless
        /// the close is reported earlier (carrying the server's code if its close frame was received).
        /// </summary>
        /// <param name="data">Data to send (pre-formatted with varint delimiters).</param>
        /// <param name="cancellationToken">Write deadline.</param>
        Task SendAsync(byte[] data, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends data through the transport using emulation mode.
        /// </summary>
        /// <param name="data">Data to send.</param>
        /// <param name="session">Session ID from connect result.</param>
        /// <param name="node">Node ID from connect result.</param>
        /// <param name="emulationEndpoint">Emulation endpoint URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task SendEmulationAsync(byte[] data, string session, string node, string emulationEndpoint, CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes the transport.
        /// </summary>
        Task CloseAsync();
    }

    /// <summary>
    /// Event arguments for transport closed event.
    /// </summary>
    internal class TransportClosedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the close code if available.
        /// </summary>
        public int? Code { get; }

        /// <summary>
        /// Gets the close reason.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransportClosedEventArgs"/> class.
        /// </summary>
        public TransportClosedEventArgs(int? code = null, string? reason = null)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>Thrown when the server or a proxy answers an emulation request with a non-2xx status.</summary>
    internal sealed class EmulationStatusException : Exception
    {
        public EmulationStatusException(int statusCode)
            : base($"emulation request failed: HTTP {statusCode}")
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }
}
