using System.Text;
using Confluent.Kafka;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Services;
using FalconFX.ServiceDefaults;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// 1. Service Defaults (Telemetry, Health Checks, Service Discovery)
builder.AddServiceDefaults();

// 2. Configure High-Performance Kafka Consumer for Order Ingestion
builder.AddKafkaConsumer<Null, byte[]>("kafka", settings =>
{
    settings.Config.GroupId = "matching-engine";
    settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
    settings.Config.EnableAutoCommit = true;
    settings.Config.AutoCommitIntervalMs = 1000;
    settings.Config.SocketTimeoutMs = 60000;
    settings.Config.ApiVersionRequestTimeoutMs = 10000;
    settings.Config.SessionTimeoutMs = 30000;
    settings.Config.HeartbeatIntervalMs = 3000;
    settings.Config.MaxPollIntervalMs = 300000;
});

// 2.5. Configure HFT-Tuned Kafka Producer for Executed Trades
builder.AddKafkaProducer<Null, byte[]>("kafka", settings =>
{
    settings.Config.LingerMs = 5;
    settings.Config.BatchSize = 512 * 1024; // 512 KB batch limit
    settings.Config.MessageMaxBytes = 900000; // Safely below Kafka broker 1MB default limit
    settings.Config.BatchNumMessages = 10000;
    settings.Config.Acks = Acks.Leader;

    // HFT Optimization: Disable delivery callbacks to eliminate librdkafka event queue allocations
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

// 6. Expose gRPC Endpoint for Direct High-Speed Order Ingestion
app.MapGrpcService<GrpcOrderService>();

// Health/Informational Root Endpoint
app.MapGet("/", () => "FalconFX Matching Engine active (gRPC & Kafka)");

await app.RunAsync();