using BenchmarkDotNet.Attributes;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class OrderBookBenchmark
{
    [Params(10_000, 100_000)] public int OrderCount;

    private OrderBook _orderBook = null!;
    private Order[] _orders = null!;

    [GlobalSetup]
    public void Setup()
    {
        _orderBook = new OrderBook(OrderCount + 1000);
        _orders = new Order[OrderCount];

        // წინასწარ ვამზადებთ შეწყვილებად შეკვეთებს
        for (var i = 0; i < OrderCount / 2; i++) _orders[i] = new Order(i + 1, OrderSide.Sell, 100, 10);

        for (var i = OrderCount / 2; i < OrderCount; i++) _orders[i] = new Order(i + 1, OrderSide.Buy, 100, 10);
    }

    [Benchmark]
    public int ProcessOrders_FullMatchScenario()
    {
        _orderBook.Clear();
        var tradeCount = 0;

        foreach (var t in _orders) _orderBook.ProcessOrder(t, _ => tradeCount++);

        return tradeCount;
    }
}