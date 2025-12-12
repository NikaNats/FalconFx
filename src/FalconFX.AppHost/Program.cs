using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// 1. Kafka + UI
var kafka = builder.AddKafka("kafka")
    .WithKafkaUI();

// 2. Postgres (Persistence)
// Password will be auto-generated, viewable in the Dashboard
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin(); // http://localhost:5050 - SQL Admin Panel

var tradeDb = postgres.AddDatabase("trade-db");

// 3. Redis (Real-time Ticker / Snapshots)
var redis = builder.AddRedis("redis")
    .WithRedisCommander(); // http://localhost:8081 - Redis Admin

// 4. Services
var matchingEngine = builder.AddProject<MatchingEngine>("matching-engine")
    .WithReference(kafka)
    // 🔥 This creates the dependency. Engine won't start until Kafka is "Healthy"
    .WaitFor(kafka); // Engine is now a PRODUCER too

var marketMaker = builder.AddProject<MarketMaker>("market-maker")
    .WithReference(kafka)
    .WaitFor(kafka);

// 5. NEW: Trade Processor
// We wait for Kafka and DB to be ready before starting this worker
var tradeProcessor = builder.AddProject<TradeProcessor>("trade-processor")
    .WithReference(kafka)
    .WithReference(tradeDb)
    .WithReference(redis)
    .WaitFor(kafka)
    .WaitFor(tradeDb);

// 6. Gateway
var gateway = builder.AddProject<FalconFX_Gateway>("gateway")
    .WithReference(redis) // Needs connection to Redis
    .WaitFor(redis)
    .WithExternalHttpEndpoints(); // Allow browser access

builder.Build().Run();