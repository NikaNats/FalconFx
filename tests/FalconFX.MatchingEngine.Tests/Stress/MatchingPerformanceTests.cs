using System.Diagnostics;
using FalconFX.MatchingEngine.Models;
using FluentAssertions;

namespace FalconFX.MatchingEngine.Tests.Stress;

public class MatchingPerformanceTests
{
    [Fact]
    public void OrderBook_1MillionOrders_ShouldProcessUnder200Milliseconds()
    {
        // Arrange: 2M pool size
        var orderBook = new OrderBook(90, 110, 2_000_000);
        var tradesCount = 0;

        // Setup 500,000 Sells @ 100
        for (var i = 1; i <= 500_000; i++) orderBook.ProcessOrder(new Order(i, OrderSide.Sell, 100, 10), _ => { });

        var sw = Stopwatch.StartNew();

        // Act: Process 500,000 Buys @ 100
        for (var i = 500_001; i <= 1_000_000; i++)
            orderBook.ProcessOrder(new Order(i, OrderSide.Buy, 100, 10), _ => tradesCount++);

        sw.Stop();

        // Assert
        tradesCount.Should().Be(500_000);
        sw.ElapsedMilliseconds.Should()
            .BeLessThan(200, $"1,000,000 შეკვეთის დამუშავებას დასჭირდა {sw.ElapsedMilliseconds}ms");
    }
}