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
    private const int MaxPooledSymbols = 10_000;
    private const int MaxSymbolChars = 64;

    // OpenTelemetry Metrics (2026 Observability Standard)
    private static readonly Meter GatewayMeter = new("FalconFX.Gateway");

    private static readonly Counter<long> UpdatesReceivedCounter =
        GatewayMeter.CreateCounter<long>("gateway.updates.received", "updates",
            "Total market updates received from Redis");

    private static readonly Counter<long> UpdatesBroadcastedCounter =
        GatewayMeter.CreateCounter<long>("gateway.updates.broadcasted", "broadcasts",
            "Total updates pushed to SignalR clients");

    // .NET 9/10 Alternate Lookup Dictionary for Zero-Allocation ReadOnlySpan<char> string pooling
    private static readonly ConcurrentDictionary<string, string> SymbolPool = new();

    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> SymbolLookup =
        SymbolPool.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly IConfiguration _config;
    private readonly IHubContext<MarketHub, IMarketClient> _hubContext;
    private readonly ILogger<RedisSubscriber> _logger;

    // Channel decouples Redis Pub/Sub receiving from SignalR WebSocket broadcasting (DropOldest prevents backpressure)
    private readonly Channel<(string Symbol, long Price)> _marketChannel;
    private readonly IConnectionMultiplexer _redis;

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
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogRedisSubscriberStarting();

        var subscriber = _redis.GetSubscriber();
        var channel = RedisChannel.Literal("market_updates");
        _logger.LogListeningToRedisChannel("market_updates");

        await subscriber.SubscribeAsync(channel, (_, message) => ProcessMessage(message))
            .ConfigureAwait(false);

        try
        {
            // Dedicated Task for broadcasting to SignalR clients (Eliminates ThreadPool task flooding)
            var broadcastTask = Task.Factory.StartNew(
                    () => SignalRBroadcastLoopAsync(stoppingToken),
                    stoppingToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();

            await broadcastTask.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await subscriber.UnsubscribeAsync(channel).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort subscription cleanup during teardown
            }
        }
    }

    internal void ProcessMessage(RedisValue message)
    {
        if (message.IsNullOrEmpty) return;

        try
        {
            ReadOnlyMemory<byte> memory = message;
            var span = memory.Span;

            var colonIdx = span.IndexOf((byte)':');
            if (colonIdx <= 0 || colonIdx == span.Length - 1) return;

            var symbolBytes = span[..colonIdx];
            var priceBytes = span[(colonIdx + 1)..];

            // Require the ENTIRE price slice to be consumed: rejects "10550abc"
            if (!Utf8Parser.TryParse(priceBytes, out long price, out var bytesConsumed) ||
                bytesConsumed != priceBytes.Length)
                return;

            if (Encoding.UTF8.GetMaxCharCount(symbolBytes.Length) > MaxSymbolChars)
            {
                _logger.LogBadMessageFormat("symbol is too long");
                return;
            }

            Span<char> charBuffer = stackalloc char[MaxSymbolChars];
            var charCount = Encoding.UTF8.GetChars(symbolBytes, charBuffer);
            ReadOnlySpan<char> symbolChars = charBuffer[..charCount];

            if (!SymbolLookup.TryGetValue(symbolChars, out var symbol))
            {
                symbol = symbolChars.ToString();
                // Bounded pool: an attacker cannot grow this dictionary without limit
                if (SymbolPool.Count < MaxPooledSymbols)
                    SymbolPool.TryAdd(symbol, symbol);
            }

            UpdatesReceivedCounter.Add(1);

            // Push update to Channel without spawning unmonitored ThreadPool Tasks
            _marketChannel.Writer.TryWrite((symbol, price));
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
            while (_marketChannel.Reader.TryRead(out var update))
                try
                {
                    await _hubContext.Clients.All.ReceiveMarketUpdate(update.Symbol, update.Price)
                        .ConfigureAwait(false);

                    UpdatesBroadcastedCounter.Add(1);
                }
                catch (Exception ex)
                {
                    // A disconnected web client must not kill the broadcast loop, but the
                    // failure still needs to be visible in telemetry.
                    _logger.LogBroadcastFailure(ex.Message);
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
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

    [LoggerMessage(EventId = 413, Level = LogLevel.Warning, Message = "SignalR broadcast failed: {Message}")]
    public static partial void LogBroadcastFailure(this ILogger logger, string message);
}
