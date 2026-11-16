using BenchmarkDotNet.Attributes;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.Benchmarks.Benchmarks;

[MemoryDiagnoser] // verifies GC allocation (must be exactly 0 B)
[RankColumn]
public class MatchingBenchmark
{
    // Delegate created once and cached (0 heap allocation)
    private static readonly TradeCallback CachedCallback = OnTradeStatic;

    private OrderBook _book = null!;
    private Order[] _orders = null!;

    [Params(1000)] public int OrderCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _book = new OrderBook(100_000);
        _orders = new Order[OrderCount];

        var random = new Random(42); // fixed seed for deterministic results

        for (var i = 0; i < OrderCount; i++)
        {
            var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
            var price = random.Next(90, 110); // tight spread (90-110) for frequent matches
            _orders[i] = new Order(i + 1, side, price, 10);
        }
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public void MatchOrders()
    {
        // Clear without reallocating (only indexes are reset)
        _book.Clear();

        var orders = _orders;
        var callback = CachedCallback;

        for (var i = 0; i < orders.Length; i++) _book.ProcessOrder(orders[i], callback);
    }

    private static void OnTradeStatic(Trade trade)
    {
        // No-op (empty method for benchmarking)
    }
}
