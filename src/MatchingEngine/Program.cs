using FalconFX.ServiceDefaults; // References your shared project
using MatchingEngine;
using MatchingEngine.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// 1. 🔥 Add Aspire Service Defaults 
// This wires up OpenTelemetry, Metrics, and Service Discovery automatically.
builder.AddServiceDefaults();

// 2. Register the Worker as a Singleton
// We register it as Singleton first so we can retrieve it manually for the simulation loop below.
builder.Services.AddSingleton<EngineWorker>();

// 3. Add it as a Hosted Service so it starts automatically
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

var host = builder.Build();

// --- ⚡ SIMULATION LOGIC START ---

// Retrieve the engine instance so we can push orders to it directly
var engine = host.Services.GetRequiredService<EngineWorker>();

// Run the traffic generator in a background thread
// In a real app, this logic would come from the 'MarketMaker' project via gRPC.
_ = Task.Run(async () =>
{
    // Give the engine a moment to warm up/start
    await Task.Delay(2000); 
    
    Console.WriteLine("⚡ Starting Traffic Simulation...");
    
    var random = new Random();
    
    // We will generate 200,000 orders for this test run
    for (var i = 0; i < 200_000; i++)
    {
        var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        
        // Random price between 90 and 110 (Matching your array bounds)
        var price = random.Next(90, 111); 

        var order = new Order(i, side, price, 10);
        
        // Push to the Engine's Channel
        engine.EnqueueOrder(order);

        // OPTIONAL: Micro-sleep to make the graph in Aspire Dashboard look pretty (less spiky)
        // Remove this line for maximum raw throughput testing.
        if (i % 500 == 0) 
        {
            await Task.Delay(1); 
        }
    }

    Console.WriteLine("✅ Simulation Traffic Sent.");
});

// --- ⚡ SIMULATION LOGIC END ---

// Run the application (This blocks until stopped)
await host.RunAsync();