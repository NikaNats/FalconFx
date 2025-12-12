extern alias MatchingEngineAlias;
using MatchingEngineAlias::MatchingEngine;
using MatchingEngineAlias::MatchingEngine.Models;
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
        Assert.Single(trades); // 1 გარიგება
        Assert.Equal(10, trades[0].Quantity);
        Assert.Equal(100, trades[0].Price);

        var (bids, asks) = book.GetDepths();
        Assert.Equal(0, bids); // წიგნი ცარიელი უნდა იყოს
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
        Assert.Equal(10, trades[0].Quantity); // მხოლოდ 10 იყო გასაყიდი

        var (bids, asks) = book.GetDepths();
        Assert.Equal(0, asks); // გამყიდველი გაქრა
        Assert.Equal(1, bids); // მყიდველი დარჩა
        // დარჩენილი მყიდველის რაოდენობა უნდა იყოს 5
    }

    [Fact]
    public void PriceTimePriority_Should_MatchBestPriceFirst()
    {
        // Arrange
        var book = new OrderBook();
        var trades = new List<Trade>();

        // Sellers: 
        // User A: Sells @ 100 (Best price)
        // User B: Sells @ 101 (Worse price)
        book.ProcessOrder(new Order(1, OrderSide.Sell, 100, 10), t => trades.Add(t));
        book.ProcessOrder(new Order(2, OrderSide.Sell, 101, 10), t => trades.Add(t));

        // Act
        // Buyer wants to buy @ 102 (willing to pay more)
        // Should match with 100 first!
        var buyOrder = new Order(3, OrderSide.Buy, 102, 5);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(100, trades[0].Price); // უნდა ეყიდა 100-ად და არა 101-ად ან 102-ად
        Assert.Equal(1, trades[0].MakerOrderId); // User A
    }

    // 1. Correctness: Price-Time Priority (FIFO)
    // In HFT, if two people offer the same price, the one who came first MUST fill first.
    [Fact]
    public void FIFO_Priority_Should_MatchEarliestOrderFirst()
    {
        var book = new OrderBook();
        var trades = new List<Trade>();

        // 1. User A places Sell Order @ 100 (Time 0)
        book.ProcessOrder(new Order(101, OrderSide.Sell, 100, 10), t => trades.Add(t));

        // 2. User B places Sell Order @ 100 (Time 1) - Same price, later time
        book.ProcessOrder(new Order(102, OrderSide.Sell, 100, 10), t => trades.Add(t));

        // Act: Buyer wants 10 units @ 100
        book.ProcessOrder(new Order(201, OrderSide.Buy, 100, 10), t => trades.Add(t));

        // Assert
        Assert.Single(trades);
        Assert.Equal(101, trades[0].MakerOrderId); // MUST be User A, not User B

        // Verify User B is still in the book
        var (bids, asks) = book.GetDepths();
        Assert.Equal(1, asks); // 1 order remaining on ask side
    }

    // 2. Safety: Memory Pool Logic
    // You are manually managing array indices. We must ensure we don't crash when the pool fills up
    // and that 'Clear' actually resets the pointers correctly.
    [Fact]
    public void MemoryPool_Should_ReuseNodes_AfterClearing()
    {
        // Create small pool to force reuse logic
        var book = new OrderBook(100);

        // Fill the pool partially with different prices to fill levels
        for (var i = 0; i < 20; i++) book.ProcessOrder(new Order(i, OrderSide.Buy, 90 + i, 1), _ => { });

        // Verify counts
        var (bids, asks) = book.GetDepths();
        Assert.Equal(20, bids); // 20 different price levels

        // Act: Clear the book (reset pointers)
        book.Clear();
        (bids, asks) = book.GetDepths();
        Assert.Equal(0, bids);

        // Act: Fill again. If pointers were broken, this would throw IndexOutOfRange or corrupt data
        for (var i = 0; i < 20; i++) book.ProcessOrder(new Order(i + 100, OrderSide.Buy, 90 + i, 1), _ => { });

        (bids, asks) = book.GetDepths();
        Assert.Equal(20, bids);
    }

    // 3. Logic: Self-Trade Prevention (Optional but recommended)
    // Or at least ensuring crossing the spread results in immediate execution
    [Fact]
    public void MarketOrder_Should_Sweep_MultipleLevels()
    {
        var book = new OrderBook();
        var trades = new List<Trade>();

        // Setup liquidity: Sells @ 100, 101, 102
        book.ProcessOrder(new Order(1, OrderSide.Sell, 100, 10), _ => { });
        book.ProcessOrder(new Order(2, OrderSide.Sell, 101, 10), _ => { });
        book.ProcessOrder(new Order(3, OrderSide.Sell, 102, 10), _ => { });

        // Act: Big Buy Order (Size 25) @ 105 (Aggressive)
        var buyOrder = new Order(99, OrderSide.Buy, 105, 25);
        book.ProcessOrder(buyOrder, t => trades.Add(t));

        // Assert
        Assert.Equal(3, trades.Count);

        // Trade 1: 10 units @ 100
        Assert.Equal(100, trades[0].Price);
        Assert.Equal(10, trades[0].Quantity);

        // Trade 2: 10 units @ 101
        Assert.Equal(101, trades[1].Price);
        Assert.Equal(10, trades[1].Quantity);

        // Trade 3: 5 units @ 102
        Assert.Equal(102, trades[2].Price);
        Assert.Equal(5, trades[2].Quantity);

        // Check remaining depth
        var (bids, asks) = book.GetDepths();
        Assert.Equal(1, asks); // Only part of the order at 102 remains (5 units)
    }
}