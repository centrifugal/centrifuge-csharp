using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Result of history request.
    /// </summary>
    public class CentrifugeHistoryResult
    {
        /// <summary>
        /// Gets the publications.
        /// </summary>
        public CentrifugePublicationEventArgs[] Publications { get; }

        /// <summary>
        /// Gets the stream epoch.
        /// </summary>
        public string Epoch { get; }

        /// <summary>
        /// Gets the stream offset.
        /// </summary>
        public ulong Offset { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeHistoryResult"/> class.
        /// </summary>
        public CentrifugeHistoryResult(CentrifugePublicationEventArgs[] publications, string epoch, ulong offset)
        {
            Publications = publications ?? Array.Empty<CentrifugePublicationEventArgs>();
            Epoch = epoch ?? string.Empty;
            Offset = offset;
        }
    }

    /// <summary>
    /// Result of presence request.
    /// </summary>
    public class CentrifugePresenceResult
    {
        /// <summary>
        /// Gets the map of client IDs to client information.
        /// </summary>
        public IReadOnlyDictionary<string, CentrifugeClientInfo> Clients { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugePresenceResult"/> class.
        /// </summary>
        public CentrifugePresenceResult(IReadOnlyDictionary<string, CentrifugeClientInfo> clients)
        {
            Clients = clients ?? new Dictionary<string, CentrifugeClientInfo>();
        }
    }

    /// <summary>
    /// Result of presence stats request.
    /// </summary>
    public class CentrifugePresenceStatsResult
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
        /// Initializes a new instance of the <see cref="CentrifugePresenceStatsResult"/> class.
        /// </summary>
        public CentrifugePresenceStatsResult(uint numClients, uint numUsers)
        {
            NumClients = numClients;
            NumUsers = numUsers;
        }
    }

    /// <summary>
    /// Result of RPC request.
    /// </summary>
    public class CentrifugeRpcResult
    {
        /// <summary>
        /// Gets the RPC result data.
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeRpcResult"/> class.
        /// </summary>
        public CentrifugeRpcResult(ReadOnlyMemory<byte> data)
        {
            Data = data;
        }
    }
}
