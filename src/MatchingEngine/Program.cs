using FalconFX.ServiceDefaults;
using MatchingEngine;
using MatchingEngine.Services;

// Shared project reference

var builder = WebApplication.CreateBuilder(args);

// 1. Add Aspire Defaults (Metrics, Tracing, HealthChecks)
builder.AddServiceDefaults();

// 2. Add gRPC Framework
builder.Services.AddGrpc();

// 3. Register our Singletons
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

var app = builder.Build();

app.MapDefaultEndpoints();

// 4. Expose the gRPC Endpoint
app.MapGrpcService<GrpcOrderService>();

// Optional: Informational endpoint
app.MapGet("/", () => "FalconFX Matching Engine is running via gRPC");

await app.RunAsync();