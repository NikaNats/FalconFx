using BenchmarkDotNet.Attributes;
using MatchingEngine.Models;

namespace MatchingEngine;

[MemoryDiagnoser]
public class MatchingBenchmark
{
    private OrderBook _book;
    private Order[] _orders;

    // 🔥 FIX: დელეგატი შექმნილია ერთხელ და შენახულია!
    private static readonly TradeCallback _cachedCallback = OnTradeStatic;

    [GlobalSetup]
    public void Setup()
    {
        _book = new OrderBook();
        _orders = new Order[1000];
        
        var random = new Random(42);
        for (int i = 0; i < 1000; i++)
        {
            var side = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
            var price = random.Next(90, 110);
            _orders[i] = new Order(i, side, price, 10);
        }
    }

    [Benchmark]
    public void Match1000Orders()
    {
        _book.Clear();
        
        foreach (var order in _orders)
        {
            // 🔥 FIX: გადავცემთ შენახულ ცვლადს და არა მეთოდის სახელს
            _book.ProcessOrder(order, _cachedCallback);
        }
    }

    private static void OnTradeStatic(Trade trade) 
    { 
        // No-op
    }
}