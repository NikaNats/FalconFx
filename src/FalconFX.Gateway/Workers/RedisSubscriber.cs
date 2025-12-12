using FalconFX.Gateway.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace FalconFX.Gateway.Workers;

public class RedisSubscriber(
    ILogger<RedisSubscriber> logger,
    IConnectionMultiplexer redis,
    IHubContext<MarketHub, IMarketClient> hubContext) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        logger.LogInformation("🎧 Gateway listening to Redis channel 'market_updates'...");

        // Subscribe to the channel populated by TradeProcessor
        await subscriber.SubscribeAsync("market_updates", async (channel, message) =>
        {
            // Offload to ThreadPool to not block the Redis listener thread
            _ = ProcessMessage(message);
        });

        // Keep service alive
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessMessage(RedisValue message)
    {
        try
        {
            // Payload format: "SYMBOL:PRICE" (e.g., "EURUSD:10050")
            // In a real app, use System.Text.Json with Source Generators for zero-alloc serialization
            // For now, String Split is fast enough for this MVP.

            var payload = message.ToString();
            // Micro-optimization: Span-based parsing would be better here for HFT, 
            // but let's keep it readable for now.
            var parts = payload.Split(':');

            if (parts.Length == 2 && long.TryParse(parts[1], out var price))
            {
                var symbol = parts[0];
                // Push to Clients
                await hubContext.Clients.All.ReceiveMarketUpdate(symbol, price);
            }
        }
        catch (Exception ex)
        {
            // Don't crash the loop
            logger.LogWarning($"Bad message format: {ex.Message}");
        }
    }
}