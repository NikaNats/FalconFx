using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;
using FalconFX.Gateway.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace FalconFX.Gateway.Workers;

/// <summary>
///     High-speed Gateway Redis Subscriber worker node.
///     Consumes market updates from Redis Pub/Sub using UTF-8 zero-allocation parsing
///     and streams live prices to SignalR web clients without ThreadPool starvation.
/// </summary>
public sealed class RedisSubscriber : BackgroundService
{
    // OpenTelemetry Metrics (2026 Observability Standard)
    private static readonly Meter GatewayMeter = new("FalconFX.Gateway");
    private static readonly Counter<long> UpdatesReceivedCounter =
        GatewayMeter.CreateCounter<long>("gateway.updates.received", "updates", "Total market updates received from Redis");
    private static readonly Counter<long> UpdatesBroadcastedCounter =
        GatewayMeter.CreateCounter<long>("gateway.updates.broadcasted", "broadcasts", "Total updates pushed to SignalR clients");

    // .NET 9/10 Alternate Lookup Dictionary for Zero-Allocation ReadOnlySpan<char> string pooling
    private static readonly ConcurrentDictionary<string, string> SymbolPool = new();
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> SymbolLookup =
        SymbolPool.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly IConfiguration _config;
    private readonly IHubContext<MarketHub, IMarketClient> _hubContext;
    private readonly ILogger<RedisSubscriber> _logger;
    private readonly IConnectionMultiplexer _redis;

    // Channel decouples Redis Pub/Sub receiving from SignalR WebSocket broadcasting (DropOldest prevents backpressure)
    private readonly Channel<(string Symbol, long Price)> _marketChannel;

    public RedisSubscriber(
        ILogger<RedisSubscriber> logger,
        IConnectionMultiplexer redis,
        IHubContext<MarketHub, IMarketClient> hubContext,
        IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _marketChannel = Channel.CreateBounded<(string Symbol, long Price)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogRedisSubscriberStarting();

        var subscriber = _redis.GetSubscriber();
        _logger.LogListeningToRedisChannel("market_updates");

        // Subscribe to Redis Pub/Sub channel
        await subscriber.SubscribeAsync(
            RedisChannel.Literal("market_updates"),
            (_, message) => ProcessMessage(message)).ConfigureAwait(false);

        // Dedicated Task for broadcasting to SignalR clients (Eliminates ThreadPool task flooding)
        var broadcastTask = Task.Factory.StartNew(
            () => SignalRBroadcastLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        await broadcastTask.ConfigureAwait(false);
    }

    private void ProcessMessage(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;

        try
        {
            ReadOnlyMemory<byte> memory = message;
            ReadOnlySpan<byte> span = memory.Span;

            var colonIdx = span.IndexOf((byte)':');
            if (colonIdx > 0)
            {
                var symbolBytes = span[..colonIdx];
                var priceBytes = span[(colonIdx + 1)..];

                if (Utf8Parser.TryParse(priceBytes, out long price, out _))
                {
                    Span<char> charBuffer = stackalloc char[32];
                    var charCount = Encoding.UTF8.GetChars(symbolBytes, charBuffer);
                    ReadOnlySpan<char> symbolChars = charBuffer[..charCount];

                    if (!SymbolLookup.TryGetValue(symbolChars, out var symbol))
                    {
                        symbol = symbolChars.ToString();
                        SymbolPool[symbol] = symbol;
                    }

                    UpdatesReceivedCounter.Add(1);

                    // Push update to Channel without spawning unmonitored ThreadPool Tasks
                    _marketChannel.Writer.TryWrite((symbol, price));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogBadMessageFormat(ex.Message);
        }
    }

    private async Task SignalRBroadcastLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _marketChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_marketChannel.Reader.TryRead(out var update))
                {
                    try
                    {
                        await _hubContext.Clients.All.ReceiveMarketUpdate(update.Symbol, update.Price)
                            .ConfigureAwait(false);

                        UpdatesBroadcastedCounter.Add(1);
                    }
                    catch
                    {
                        // Suppress web client socket disconnect errors
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }
}


internal static partial class RedisSubscriberLogExtensions
{
    [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "🚀 Gateway Redis Subscriber starting...")]
    public static partial void LogRedisSubscriberStarting(this ILogger logger);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information,
        Message = "🎧 Gateway listening to Redis channel '{Channel}'...")]
    public static partial void LogListeningToRedisChannel(this ILogger logger, string channel);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Bad market_updates message format: {Reason}")]
    public static partial void LogBadMessageFormat(this ILogger logger, string reason);
}