using System.Reflection;
using Centrifugal.Centrifuge.Transports;
using Xunit;

namespace Centrifugal.Centrifuge.Tests
{
    /// <summary>Triggers a client's no-ping timeout as its ping timer would, for a given transport.</summary>
    internal static class NoPing
    {
        /// <summary>Triggers the no-ping timeout for the client's current transport, which must exist.</summary>
        public static void Fire(CentrifugeClient client)
        {
            var transport = Transport(client);
            Assert.NotNull(transport);
            client.NoPing(transport);
        }

        /// <summary>Reads the client's current transport; null when it has none.</summary>
        public static ITransport? Transport(CentrifugeClient client) =>
            (ITransport?)typeof(CentrifugeClient)
                .GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(client);
    }
}
