using System.Runtime.CompilerServices;
using System.Text;
using Confluent.Kafka;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Services;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// 1. Configure Kafka Consumer (manual offset store for at-least-once guarantees)
builder.AddKafkaConsumer<Ignore, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "matching-engine";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
    settings.Config.EnableAutoCommit = true;
    settings.Config.AutoCommitIntervalMs = 1000;
    settings.Config.EnableAutoOffsetStore = false; // CRITICAL: store only after the engine processed the order
    settings.Config.SocketTimeoutMs = 60000;
    settings.Config.ApiVersionRequestTimeoutMs = 10000;
    settings.Config.SessionTimeoutMs = 30000;
    settings.Config.HeartbeatIntervalMs = 3000;
    settings.Config.MaxPollIntervalMs = 300000;
});

// 2. Configure Typed Kafka Producer for Executed Trades
builder.AddKafkaProducer<long, TradeExecuted>(
    "kafka",
    settings =>
    {
        settings.Config.LingerMs = 5;
        settings.Config.BatchSize = 512 * 1024; // 512KB
        settings.Config.MessageMaxBytes = 5_242_880; // 5MB limit
        settings.Config.BatchNumMessages = 10_000;

        settings.Config.QueueBufferingMaxMessages = 1_000_000;
        settings.Config.QueueBufferingMaxKbytes = 512_000;

        settings.Config.Acks = Acks.Leader;
        settings.Config.EnableDeliveryReports = true; // observable delivery errors via cached handler
        settings.Config.CompressionType = CompressionType.Lz4;
    },
    producerBuilder =>
    {
        producerBuilder.SetKeySerializer(Serializers.Int64);
        producerBuilder.SetValueSerializer(SafeTradeExecutedSerializer.Instance);
    }
);

builder.Services.AddGrpc();
builder.Services.AddSingleton<EngineWorker>();

// Registration order is intentional: the host stops hosted services in reverse order.
// KafkaWorker must stop *after* EngineWorker so that every offset the engine completed
// (including its final in-flight orders) is drained and committed before the consumer closes.
builder.Services.AddHostedService<KafkaWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

var app = builder.Build();

// Wire up OpenTelemetry observable counters once the EngineWorker is constructed
var engine = app.Services.GetRequiredService<EngineWorker>();
Instrumentation.RegisterObservableMetrics(
    () => engine.OrdersProcessed,
    () => engine.TradesMatched,
    () => engine.OrdersRejectedByRisk,
    () => engine.OrdersRejectedByBook);

app.MapDefaultEndpoints();
app.MapGrpcService<GrpcOrderService>();
app.MapGet("/", () => "FalconFX Matching Engine active (gRPC & Kafka)");

await app.RunAsync();

internal sealed class SafeTradeExecutedSerializer : ISerializer<TradeExecuted>
{
    public static readonly SafeTradeExecutedSerializer Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Serialize(TradeExecuted data, SerializationContext context)
    {
        if (data is null) return [];

        var size = data.CalculateSize();
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        data.WriteTo(buffer);
        return buffer;
    }
}
