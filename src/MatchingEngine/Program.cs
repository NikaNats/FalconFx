using MatchingEngine;
using MatchingEngine.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics; // Add this
using OpenTelemetry.Trace;   // Add this

var builder = Host.CreateApplicationBuilder(args);

// 🔥 ASPIRE 13 INTEGRATION
// This wires up OpenTelemetry, Metrics, and Health Checks automatically
builder.AddServiceDefaults();

// 2. 🔥 REGISTER CUSTOM METRICS & TRACES HERE
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(Instrumentation.ServiceName)) // Listen to our custom Meter
    .WithTracing(tracing => tracing
        .AddSource(Instrumentation.ServiceName)); // Listen to our custom ActivitySource

// Register the EngineWorker
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

var host = builder.Build();

// If you need access to the worker for the simulation loop in the main thread:
var engine = host.Services.GetRequiredService<EngineWorker>();

// Start the host in the background
var hostTask = host.RunAsync();

// --- SIMULATION LOGIC ---
// Note: In a real Aspire deployment, this traffic generation usually moves 
// to a separate LoadTest project or the MarketMaker project.
// For self-contained testing, keeping it here is fine.

Console.WriteLine("Generating traffic...");

var producerTask = Task.Run(() => 
{
    var random = new Random();
    // Reduced count slightly for visualization in Dashboard, increase for benchmarks
    for (int i = 0; i < 100_000; i++) 
    {
        var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        var price = random.Next(90, 110);
        
        var order = new Order(i, side, price, 10);
        engine.EnqueueOrder(order);
        
        // Slight delay to visualize flow in Aspire Dashboard logs
        if (i % 100 == 0) Thread.Sleep(10); 
    }
    Console.WriteLine("✅ Orders sent!");
});

await Task.WhenAll(producerTask);

// Keep alive
await hostTask;
