using System;
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
        TransportType Type { get; }

        /// <summary>
        /// Gets the transport name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Event raised when transport is opened and ready to send/receive.
        /// </summary>
        event EventHandler? Opened;

        /// <summary>
        /// Event raised when a message is received.
        /// </summary>
        event EventHandler<byte[]>? MessageReceived;

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
        /// <param name="cancellationToken">Cancellation token.</param>
        Task OpenAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends data through the transport.
        /// </summary>
        /// <param name="data">Data to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task SendAsync(byte[] data, CancellationToken cancellationToken = default);

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
        /// Gets the exception if the transport closed due to an error.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransportClosedEventArgs"/> class.
        /// </summary>
        public TransportClosedEventArgs(int? code = null, string? reason = null, Exception? exception = null)
        {
            Code = code;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }
    }
}
