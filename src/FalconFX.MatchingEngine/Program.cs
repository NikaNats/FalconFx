using Confluent.Kafka;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Services;
using FalconFX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Aspire Defaults (Metrics, Tracing, HealthChecks)
builder.AddServiceDefaults();

// 2. Configure Kafka Consumer for Orders (Null Key for zero string-allocations)
builder.AddKafkaConsumer<Null, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "matching-engine";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
    settings.Config.EnableAutoCommit = false;
    settings.Config.SocketTimeoutMs = 60000;
    settings.Config.ApiVersionRequestTimeoutMs = 10000;
    settings.Config.SessionTimeoutMs = 30000;
    settings.Config.HeartbeatIntervalMs = 3000;
    settings.Config.MaxPollIntervalMs = 300000;
});

// 2.5. Configure Kafka Producer for Trades (HFT Tuned)
builder.AddKafkaProducer<Null, byte[]>("kafka", settings =>
{
    // High throughput settings for trade reporting
    settings.Config.LingerMs = 5;
    settings.Config.BatchSize = 1024 * 1024; // 1 MB
    settings.Config.BatchNumMessages = 10000;
    settings.Config.Acks = Acks.Leader;

    // 🔥 HFT Optimization: Disable delivery report callbacks for trades
    settings.Config.EnableDeliveryReports = false;
});

// 3. Add gRPC Framework
builder.Services.AddGrpc();

// 4. Register EngineWorker as Singleton and Background HostedService
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

// 5. Add Kafka Worker (Consumes orders from Kafka)
builder.Services.AddHostedService<KafkaWorker>();

var app = builder.Build();

app.MapDefaultEndpoints();

// 6. Expose the gRPC Endpoint for Direct High-Speed Order Streaming
app.MapGrpcService<GrpcOrderService>();

// Informational endpoint
app.MapGet("/", () => "🚀 FalconFX Matching Engine is running via gRPC & Kafka");

await app.RunAsync();