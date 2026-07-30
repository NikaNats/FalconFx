using BenchmarkDotNet.Attributes;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine;

[MemoryDiagnoser] // ამოწმებს GC Allocation-ს (უნდა იყოს ზუსტად 0 B)
[RankColumn] // ამატებს Rank (ადგილების) სვეტს შედეგებში
public class MatchingBenchmark
{
    // 🔥 დელეგატი შექმნილია 1-ხელ და ქეშირებულია (0 Heap Allocation)
    private static readonly TradeCallback CachedCallback = OnTradeStatic;

    private OrderBook _book = null!;
    private Order[] _orders = null!;

    [Params(1000)] public int OrderCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // ინიციალიზაცია 100,000 ტევადობით
        _book = new OrderBook(100_000);
        _orders = new Order[OrderCount];

        var random = new Random(42); // ფიქსირებული Seed დეტერმინისტული შედეგისთვის

        for (var i = 0; i < OrderCount; i++)
        {
            var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
            var price = random.Next(90, 110); // მჭიდრო Spread (90-110) გახშირებული Match-ებისთვის
            _orders[i] = new Order(i + 1, side, price, 10);
        }
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public void MatchOrders()
    {
        // გასუფთავება მასივის რე-ალოკაციის გარეშე (მხოლოდ ინდექსების განულება)
        _book.Clear();

        var orders = _orders;
        var callback = CachedCallback;

        for (var i = 0; i < orders.Length; i++) _book.ProcessOrder(orders[i], callback);
    }

    private static void OnTradeStatic(Trade trade)
    {
        // No-op (ცარიელი მეთოდი ბენჩმარკინგისთვის)
    }
}