using System;
using System.Text;
using System.Threading.Tasks;
using Centrifugal.Centrifuge;
using Microsoft.Extensions.Logging;

namespace Centrifuge.Examples
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Centrifuge C# SDK Example");
            Console.WriteLine("========================\n");

            // Create a logger that writes to console
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddConsole()
                    .SetMinimumLevel(LogLevel.Debug);
            });
            var logger = loggerFactory.CreateLogger<CentrifugeClient>();

            // Configure client options
            var options = new CentrifugeClientOptions
            {
                Logger = logger,
                // Add your connection token here if needed
                // Token = "your-jwt-token",
            };

            // Create client with 'await using' for proper async disposal
            await using var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);

            // Setup client event handlers
            client.StateChanged += (sender, e) =>
            {
                Console.WriteLine($"[Client] State: {e.OldState} -> {e.NewState}");
            };

            client.Connecting += (sender, e) =>
            {
                Console.WriteLine($"[Client] Connecting: {e.Code} - {e.Reason}");
            };

            client.Connected += (sender, e) =>
            {
                Console.WriteLine($"[Client] Connected!");
                Console.WriteLine($"  Client ID: {e.ClientId}");
                Console.WriteLine($"  Transport: {e.Transport}");
            };

            client.Disconnected += (sender, e) =>
            {
                Console.WriteLine($"[Client] Disconnected: {e.Code} - {e.Reason}");
            };

            client.Error += (sender, e) =>
            {
                Console.WriteLine($"[Client] Error: {e.Type} - {e.Message}");
            };

            try
            {
                // Connect to server (non-blocking)
                Console.WriteLine("Connecting to server...");
                client.Connect();

                // Create subscription
                var subscription = client.NewSubscription("chat");

                // Setup subscription event handlers
                subscription.StateChanged += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] State: {e.OldState} -> {e.NewState}");
                };

                subscription.Subscribing += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Subscribing: {e.Code} - {e.Reason}");
                };

                subscription.Subscribed += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Subscribed!");
                    if (e.WasRecovering)
                    {
                        Console.WriteLine($"  Recovery attempted, recovered: {e.Recovered}");
                    }
                };

                subscription.Unsubscribed += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Unsubscribed: {e.Code} - {e.Reason}");
                };

                subscription.Publication += (sender, e) =>
                {
                    var data = Encoding.UTF8.GetString(e.Data.Span);
                    Console.WriteLine($"[Subscription] Publication received:");
                    Console.WriteLine($"  Channel: {e.Channel}");
                    Console.WriteLine($"  Data: {data}");
                    if (e.Offset.HasValue)
                    {
                        Console.WriteLine($"  Offset: {e.Offset}");
                    }
                    if (e.Info != null)
                    {
                        Console.WriteLine($"  From: {e.Info.Value.Client} (user: {e.Info.Value.User})");
                    }
                };

                subscription.Join += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Client joined: {e.Info.Client}");
                };

                subscription.Leave += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Client left: {e.Info.Client}");
                };

                subscription.Error += (sender, e) =>
                {
                    Console.WriteLine($"[Subscription] Error: {e.Type} - {e.Message}");
                };

                // Subscribe to channel (non-blocking)
                Console.WriteLine("\nSubscribing to channel 'chat'...");
                subscription.Subscribe();

                // Publish a message (waits for subscription automatically)
                Console.WriteLine("\nPublishing message...");
                try
                {
                    var messageData = Encoding.UTF8.GetBytes("{\"text\":\"Hello from C# SDK!\"}");
                    await subscription.PublishAsync(messageData);
                    Console.WriteLine("Published message");
                }
                catch (CentrifugeException ex) when (ex.Code == CentrifugeErrorCodes.Timeout)
                {
                    Console.WriteLine($"Publish timed out: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Publish failed: {ex.Message}");
                }

                // Get presence
                Console.WriteLine("\nFetching presence...");
                try
                {
                    var presence = await subscription.PresenceAsync();
                    Console.WriteLine($"Clients in channel: {presence.Clients.Count}");
                    foreach (var (clientId, clientInfo) in presence.Clients)
                    {
                        Console.WriteLine($"  - {clientId}: user={clientInfo.User}");
                    }

                    var stats = await subscription.PresenceStatsAsync();
                    Console.WriteLine($"Presence stats: {stats.NumClients} clients, {stats.NumUsers} users");
                }
                catch (CentrifugeException ex) when (ex.Code == CentrifugeErrorCodes.Timeout)
                {
                    Console.WriteLine($"Presence timed out: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Presence failed: {ex.Message}");
                }

                // Get history
                Console.WriteLine("\nFetching history...");
                try
                {
                    var history = await subscription.HistoryAsync(new CentrifugeHistoryOptions
                    {
                        Limit = 10,
                        Reverse = true
                    });

                    Console.WriteLine($"History: {history.Publications.Length} messages");
                    foreach (var pub in history.Publications)
                    {
                        var data = Encoding.UTF8.GetString(pub.Data.Span);
                        Console.WriteLine($"  - {data}");
                    }
                }
                catch (CentrifugeException ex) when (ex.Code == CentrifugeErrorCodes.Timeout)
                {
                    Console.WriteLine($"History timed out: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"History failed: {ex.Message}");
                }

                // RPC example
                Console.WriteLine("\nSending RPC...");
                try
                {
                    var rpcData = Encoding.UTF8.GetBytes("{\"method\":\"getCurrentTime\"}");
                    var rpcResult = await client.RpcAsync("time", rpcData);
                    var response = Encoding.UTF8.GetString(rpcResult.Data.Span);
                    Console.WriteLine($"RPC response: {response}");
                }
                catch (CentrifugeException ex) when (ex.Code == CentrifugeErrorCodes.Timeout)
                {
                    Console.WriteLine($"RPC timed out: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RPC failed: {ex.Message}");
                }

                // Wait for user input before disconnecting
                Console.WriteLine("\nPress any key to disconnect...");
                Console.ReadKey();

                // Cleanup
                Console.WriteLine("\nUnsubscribing...");
                subscription.Unsubscribe();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error caught: {ex}");
            }

            // DisposeAsync is called automatically when exiting the 'await using' block
            Console.WriteLine("\nDisposing client (disconnecting)...");
            // Client disposal happens here automatically

            Console.WriteLine("Done!");
        }
    }
}
