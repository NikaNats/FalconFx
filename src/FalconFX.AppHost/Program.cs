using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// 1. Register the Matching Engine
// We configure it as a project. In a real scenario, this might expose
// a gRPC endpoint or a TCP socket, handled via .WithEndpoint()
var matchingEngine = builder.AddProject<MatchingEngine>("matching-engine");

// 2. Register the Market Maker
// The Market Maker needs to know where the Engine is.
// Since your current code uses in-process channels, if these are separate processes,
// you would typically need networking (gRPC/TCP).
// For now, we set up the orchestration dependency.
var marketMaker = builder.AddProject<MarketMaker>("market-maker")
    .WithReference(matchingEngine)
    .WaitFor(matchingEngine); // Aspire 13 Best Practice: Ensure Engine is up before MM starts

builder.Build().Run();