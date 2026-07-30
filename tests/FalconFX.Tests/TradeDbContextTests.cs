using FalconFX.TradeProcessor.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FalconFX.TradeProcessor.Tests;

public class TradeDbContextTests : IDisposable
{
    private readonly TradeDbContext _context;

    public TradeDbContextTests()
    {
        // ინ-მემორი ბაზის შექმნა ტესტირებისთვის
        var options = new DbContextOptionsBuilder<TradeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TradeDbContext(options);
        _context.Database.EnsureCreated();
    }

    [Fact]
    public async Task AddRange_BatchInsert1000Trades_ShouldSaveAllRecordsToDatabase()
    {
        // Arrange
        var trades = new List<TradeRecord>();
        for (int i = 0; i < 1000; i++)
        {
            trades.Add(new TradeRecord
            {
                MakerOrderId = i + 1,
                TakerOrderId = i + 1000,
                Price = 10000 + i,
                Quantity = 10,
                Symbol = "EURUSD",
                Timestamp = DateTime.UtcNow.Ticks + i
            });
        }

        // Act - ოპტიმიზებული Bulk Add (ChangeTracker გათიშული)
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        _context.Trades.AddRange(trades);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert
        var savedCount = await _context.Trades.CountAsync(TestContext.Current.CancellationToken);
        savedCount.Should().Be(1000, "1000-ვე ჩანაწერი წარმატებით უნდა შეინახოს ბაზაში");

        var firstTrade = await _context.Trades.FirstOrDefaultAsync(t => t.MakerOrderId == 1, TestContext.Current.CancellationToken);
        firstTrade.Should().NotBeNull();
        firstTrade!.Symbol.Should().Be("EURUSD");
    }

    [Fact]
    public async Task QueryTradesBySymbolAndTimestamp_ShouldUseCompositeIndexPattern()
    {
        // Arrange
        var nowTicks = DateTime.UtcNow.Ticks;
        _context.Trades.AddRange(
            new TradeRecord { Symbol = "EURUSD", Timestamp = nowTicks, Price = 100 },
            new TradeRecord { Symbol = "EURUSD", Timestamp = nowTicks + 10, Price = 101 },
            new TradeRecord { Symbol = "GBPUSD", Timestamp = nowTicks, Price = 130 }
        );
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - Time-Series მოთხოვნა (Symbol + Timestamp)
        var results = await _context.Trades
            .Where(t => t.Symbol == "EURUSD" && t.Timestamp >= nowTicks)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().HaveCount(2);
        results.All(t => t.Symbol == "EURUSD").Should().BeTrue();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}