using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;
using Xunit;

namespace FalconFX.Tests;

public class OrderBookTests
{
    [Fact]
    public void FullMatch_Should_ClearOrderBook()
    {
        // Arrange
        var book = new OrderBook();
        var trades = new List<Trade>();

        // Seller: Sells 10 @ 100
        var sellOrder = new Order(1, OrderSide.Sell, 100, 10);
        book.ProcessOrder(sellOrder, t => trades.Add(t));

        // Act
        // Buyer: Buys 10 @ 100
        var buyOrder = new Order(2, OrderSide.Buy, 100, 10);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(10, trades[0].Quantity);
        Assert.Equal(100, trades[0].Price);

        var (bids, asks) = book.GetDepths();
        Assert.Equal(0, bids);
        Assert.Equal(0, asks);
    }

    [Fact]
    public void PartialFill_Should_LeaveRemainsInBook()
    {
        // Arrange
        var book = new OrderBook();
        var trades = new List<Trade>();

        // Seller: Sells 10 @ 100
        book.ProcessOrder(new Order(1, OrderSide.Sell, 100, 10), t => trades.Add(t));

        // Act
        // Buyer: Buys 15 @ 100 (Takes 10, rests 5)
        var buyOrder = new Order(2, OrderSide.Buy, 100, 15);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(10, trades[0].Quantity);

        var (bids, asks) = book.GetDepths();
        Assert.Equal(0, asks);
        Assert.Equal(1, bids);
    }

    [Fact]
    public void PriceTimePriority_Should_MatchBestPriceFirst()
    {
        // Arrange
        var book = new OrderBook();
        var trades = new List<Trade>();

        // Sellers: 
        book.ProcessOrder(new Order(1, OrderSide.Sell, 100, 10), t => trades.Add(t));
        book.ProcessOrder(new Order(2, OrderSide.Sell, 101, 10), t => trades.Add(t));

        // Act
        var buyOrder = new Order(3, OrderSide.Buy, 102, 5);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(100, trades[0].Price);
        Assert.Equal(1, trades[0].MakerOrderId);
    }

    [Fact]
    public void FIFO_Priority_Should_MatchEarliestOrderFirst()
    {
        var book = new OrderBook();
        var trades = new List<Trade>();

        // 1. User A places Sell Order @ 100
        book.ProcessOrder(new Order(101, OrderSide.Sell, 100, 10), t => trades.Add(t));

        // 2. User B places Sell Order @ 100
        book.ProcessOrder(new Order(102, OrderSide.Sell, 100, 10), t => trades.Add(t));

        // Act
        book.ProcessOrder(new Order(201, OrderSide.Buy, 100, 10), t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(101, trades[0].MakerOrderId);

        var (_, asks) = book.GetDepths();
        Assert.Equal(1, asks);
    }

    [Fact]
    public void MemoryPool_Should_ReuseNodes_AfterClearing()
    {
        // 100 Capacity Pool
        var book = new OrderBook(poolSize: 100);

        for (var i = 0; i < 20; i++)
            book.ProcessOrder(new Order(i, OrderSide.Buy, 90 + i, 1), _ => { });

        var (bids, _) = book.GetDepths();
        Assert.Equal(20, bids);

        book.Clear();
        (bids, _) = book.GetDepths();
        Assert.Equal(0, bids);

        for (var i = 0; i < 20; i++)
            book.ProcessOrder(new Order(i + 100, OrderSide.Buy, 90 + i, 1), _ => { });

        (bids, _) = book.GetDepths();
        Assert.Equal(20, bids);
    }

    [Fact]
    public void MarketOrder_Should_Sweep_MultipleLevels()
    {
        var book = new OrderBook();
        var trades = new List<Trade>();

        book.ProcessOrder(new Order(1, OrderSide.Sell, 100, 10), _ => { });
        book.ProcessOrder(new Order(2, OrderSide.Sell, 101, 10), _ => { });
        book.ProcessOrder(new Order(3, OrderSide.Sell, 102, 10), _ => { });

        var buyOrder = new Order(99, OrderSide.Buy, 105, 25);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        Assert.Equal(3, trades.Count);

        Assert.Equal(100, trades[0].Price);
        Assert.Equal(10, trades[0].Quantity);

        Assert.Equal(101, trades[1].Price);
        Assert.Equal(10, trades[1].Quantity);

        Assert.Equal(102, trades[2].Price);
        Assert.Equal(5, trades[2].Quantity);

        var (_, asks) = book.GetDepths();
        Assert.Equal(1, asks);
    }

    // 4. ახალი ტესტი: უარყოფა ფასის ზღვარს მიღმა
    [Fact]
    public void Order_OutsidePriceBounds_Should_ReturnRejectedStatus()
    {
        var book = new OrderBook(minPrice: 90, maxPrice: 110);

        var invalidLowOrder = new Order(1, OrderSide.Buy, 85, 10);
        var invalidHighOrder = new Order(2, OrderSide.Buy, 115, 10);

        var result1 = book.ProcessOrder(invalidLowOrder, _ => { });
        var result2 = book.ProcessOrder(invalidHighOrder, _ => { });

        Assert.Equal(OrderResult.Rejected_PriceOutOfRange, result1);
        Assert.Equal(OrderResult.Rejected_PriceOutOfRange, result2);
    }

    // 5. ახალი ტესტი: მეხსიერების პულის ამოწურვა
    [Fact]
    public void Order_WhenPoolExhausted_Should_ReturnPoolExhaustedStatus()
    {
        // შევქმნათ პატარა პული (2 ელემენტიანი)
        var book = new OrderBook(minPrice: 90, maxPrice: 110, poolSize: 2);

        book.ProcessOrder(new Order(1, OrderSide.Buy, 95, 10), _ => { });
        book.ProcessOrder(new Order(2, OrderSide.Buy, 96, 10), _ => { });

        // მესამე შეკვეთამ უნდა დააბრუნოს PoolExhausted
        var result = book.ProcessOrder(new Order(3, OrderSide.Buy, 97, 10), _ => { });

        Assert.Equal(OrderResult.Rejected_PoolExhausted, result);
    }
}