using MatchingEngine;
using MatchingEngine.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ეს არის ჩვენი "Production" გაშვება
var builder = Host.CreateApplicationBuilder(args);

// სერვისის რეგისტრაცია (Singleton, რომ შეგვეძლოს წვდომა)
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineWorker>());

var host = builder.Build();

// ვიღებთ რეფერენსს რომ ორდერები გავაგზავნოთ
var engine = host.Services.GetRequiredService<EngineWorker>();

// ვუშვებთ ჰოსტს ფონურ რეჟიმში
var hostTask = host.RunAsync();

Console.WriteLine("Generating traffic...");

// --- SIMULATION (Load Test) ---
// გავუშვათ პარალელურად 2 თრედი, რომელიც ორდერებს ყრის
// ეს არის "Kafka Consumer"-ის სიმულაცია

var producerTask = Task.Run(() => 
{
    var random = new Random();
    for (int i = 0; i < 1_000_000; i++) // 1 მილიონი ორდერი!
    {
        var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        var price = random.Next(90, 110);
        
        var order = new Order(i, side, price, 10);
        engine.EnqueueOrder(order);
        
        // ცოტა "Noise" რომ რეალური იყოს
        // Thread.Sleep(1); 
    }
    Console.WriteLine("✅ 1 Million orders sent!");
});

await Task.WhenAll(producerTask);

Console.WriteLine("Press Enter to stop...");
Console.ReadLine();
