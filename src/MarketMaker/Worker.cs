using FalconFX.Protos;
using Grpc.Core;

namespace MarketMaker;

public class Worker(
    ILogger<Worker> logger,
    OrderService.OrderServiceClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Wait for Aspire to boot up the infrastructure
        await Task.Delay(2000, stoppingToken);

        // 2. THE RECONNECTION LOOP (Application-Level Resilience)
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("🔄 Connecting to Matching Engine...");

            try
            {
                // Create the stream. 
                // Using 'using' ensures that if the loop breaks, the channel closes cleanly.
                using var call = client.StreamOrders(cancellationToken: stoppingToken);
                var requestStream = call.RequestStream;

                logger.LogInformation("✅ Connected. Starting order stream...");

                var random = new Random();
                long orderIdCounter = 0;

                // 3. The Sending Loop
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Batch logic...
                    for (var i = 0; i < 100; i++)
                    {
                        orderIdCounter++;
                        // If network drops here, WriteAsync throws RpcException/IOException
                        await requestStream.WriteAsync(new SubmitOrderRequest
                        {
                            Id = orderIdCounter,
                            Side = random.Next(1, 3),
                            Price = random.Next(90, 110),
                            Quantity = 10
                        }, stoppingToken);
                    }

                    // Throttle slightly if needed, or check stats
                    if (orderIdCounter % 50_000 == 0)
                        logger.LogInformation($"⚡ Sent {orderIdCounter:N0} orders...");
                }

                // If we exit the inner loop manually, close cleanly
                await requestStream.CompleteAsync();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                logger.LogWarning("⚠️ Matching Engine is unavailable. Retrying...");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "❌ Connection dropped. Reconnecting...");
            }

            // 4. Backoff Strategy (Wait before reconnecting)
            if (!stoppingToken.IsCancellationRequested) await Task.Delay(2000, stoppingToken);
        }
    }
}