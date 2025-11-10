using System;
using Centrifugal.Centrifuge;

namespace Examples
{
    /// <summary>
    /// Example demonstrating FilterNode usage without needing Protocol namespace.
    /// </summary>
    public class FilterNodeExample
    {
        public static void Main()
        {
            // Users only need to import Centrifugal.Centrifuge namespace
            // No need for: using Centrifugal.Centrifuge.Protocol;

            var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

            // Simple equality filter
            var simpleFilter = FilterNodeBuilder.Eq("ticker", "BTC");

            // Complex filter with logical operators
            var complexFilter = FilterNodeBuilder.And(
                FilterNodeBuilder.Or(
                    FilterNodeBuilder.Eq("ticker", "BTC"),
                    FilterNodeBuilder.Eq("ticker", "ETH")
                ),
                FilterNodeBuilder.Gt("price", "50000")
            );

            // IN filter
            var inFilter = FilterNodeBuilder.In("ticker", "BTC", "ETH", "SOL");

            // Set up subscription with filter
            var options = new SubscriptionOptions
            {
                TagsFilter = complexFilter
            };

            var sub = client.NewSubscription("crypto", options);

            // Can also set filter after subscription creation
            sub.SetTagsFilter(FilterNodeBuilder.And(
                FilterNodeBuilder.Eq("exchange", "binance"),
                FilterNodeBuilder.Gt("volume", "1000000")
            ));

            Console.WriteLine("FilterNode examples created successfully!");
            Console.WriteLine("No Protocol namespace import required!");
        }
    }
}
