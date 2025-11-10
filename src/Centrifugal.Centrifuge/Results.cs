using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
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
