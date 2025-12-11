using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// 1. Add Kafka with Data Volume (Persistence) and UI
var kafka = builder.AddKafka("kafka")
    // .WithDataVolume(); // Comment out if you have permission errors on Windows/WSL
    .WithKafkaUI(); // http://localhost:8080 - ვიზუალური ადმინ პანელი

// 2. Matching Engine (Consumer)
var matchingEngine = builder.AddProject<MatchingEngine>("matching-engine")
    .WithReference(kafka)
    .WaitFor(kafka); // <--- ADD THIS: Don't start until Kafka container is Up

// 3. Market Maker (Producer)
var marketMaker = builder.AddProject<MarketMaker>("market-maker")
    .WithReference(kafka)
    .WaitFor(kafka); // <--- ADD THIS

await builder.Build().RunAsync();