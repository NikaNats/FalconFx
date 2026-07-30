using System.Collections.Concurrent;

using FalconFX.Gateway.Hubs;
using FalconFX.ServiceDefaults;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace FalconFX.Gateway.Workers;

public sealed class RedisSubscriber : BackgroundService
{
    private readonly ILogger<RedisSubscriber> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<MarketHub, IMarketClient> _hubContext;
    private readonly IConfiguration _config;

    // Symbol String Pool: ინახავს "EURUSD", "GBPUSD" string-ებს ხელახალი გამოყენებისთვის (0 Heap Allocation)
    private static readonly ConcurrentDictionary<string, string> SymbolPool = new();

    public RedisSubscriber(
        ILogger<RedisSubscriber> logger,
        IConnectionMultiplexer redis,
        IHubContext<MarketHub, IMarketClient> hubContext,
        IConfiguration config)
    {
        _logger = logger;
        _redis = redis;
        _hubContext = hubContext;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogRedisSubscriberStarting();

        var subscriber = _redis.GetSubscriber();
        _logger.LogListeningToRedisChannel("market_updates");

        // Subscribe to Redis Pub/Sub
        await subscriber.SubscribeAsync(
            RedisChannel.Literal("market_updates"),
            (_, message) => ProcessMessage(message)).ConfigureAwait(false);

        // Keep BackgroundService alive gracefully
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private void ProcessMessage(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;

        try
        {
            // 🚀 Zero-Alloc Span Parsing (არანაირი string.Split)
            var rawText = message.ToString();
            ReadOnlySpan<char> span = rawText.AsSpan();

            int colonIdx = span.IndexOf(':');
            if (colonIdx > 0)
            {
                ReadOnlySpan<char> symbolSpan = span[..colonIdx];
                ReadOnlySpan<char> priceSpan = span[(colonIdx + 1)..];

                if (long.TryParse(priceSpan, out var price))
                {
                    // Symbol String Pooling (ქეშირებული string)
                    string symbolKey = symbolSpan.ToString();
                    string symbol = SymbolPool.GetOrAdd(symbolKey, symbolKey);

                    // Push Real-Time Market Data to SignalR Web Clients
                    _ = _hubContext.Clients.All.ReceiveMarketUpdate(symbol, price);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogBadMessageFormat(ex.Message);
        }
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
internal static partial class RedisSubscriberLogExtensions
{
    [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "🚀 Gateway Redis Subscriber starting...")]
    public static partial void LogRedisSubscriberStarting(this ILogger logger);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information, Message = "🎧 Gateway listening to Redis channel '{Channel}'...")]
    public static partial void LogListeningToRedisChannel(this ILogger logger, string channel);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Bad market_updates message format: {Reason}")]
    public static partial void LogBadMessageFormat(this ILogger logger, string reason);
}