using MatchingEngine.Models;

namespace MatchingEngine.Tests;

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
}