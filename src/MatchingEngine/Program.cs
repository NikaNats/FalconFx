using Confluent.Kafka;
using FalconFX.ServiceDefaults;
using MatchingEngine;
using MatchingEngine.Services;

// Shared project reference

var builder = WebApplication.CreateBuilder(args);

// 1. Add Aspire Defaults (Metrics, Tracing, HealthChecks)
builder.AddServiceDefaults();

// 2. Configure Kafka Consumer
builder.AddKafkaConsumer<string, byte[]>("kafka", settings =>
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

// 2.5. Configure Kafka Producer for Trades
builder.AddKafkaProducer<string, byte[]>("kafka", settings =>
{
    // High throughput settings for trade reporting
    settings.Config.LingerMs = 5;
    settings.Config.BatchSize = 65536;
    settings.Config.Acks = Acks.Leader;
});

// 3. Add gRPC Framework
builder.Services.AddGrpc();

// 3. Register our Singletons
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

// Add Kafka Worker
builder.Services.AddHostedService<KafkaWorker>();

var app = builder.Build();

app.MapDefaultEndpoints();

// 4. Expose the gRPC Endpoint
app.MapGrpcService<GrpcOrderService>();

// Optional: Informational endpoint
app.MapGet("/", () => "FalconFX Matching Engine is running via gRPC");

await app.RunAsync();