using FalconFX.TradeProcessor.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FalconFX.TradeProcessor.Tests.Data;

public class TradeDbContextTests : IDisposable
{
    private readonly TradeDbContext _context;

    public TradeDbContextTests()
    {
        var options = new DbContextOptionsBuilder<TradeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TradeDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task AddRange_BatchInsert1000Trades_ShouldSaveAllRecordsToDatabase()
    {
        // Arrange
        var trades = Enumerable.Range(1, 1000).Select(i => new TradeRecord
        {
            MakerOrderId = i,
            TakerOrderId = i + 1000,
            Price = 10000 + i,
            Quantity = 10,
            Symbol = "EURUSD",
            Timestamp = DateTime.UtcNow.Ticks + i
        }).ToList();

        // Act: ოპტიმიზებული ბაჩინგი
        var cancellationToken = TestContext.Current.CancellationToken;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        _context.Trades.AddRange(trades);
        await _context.SaveChangesAsync(cancellationToken);

        // Assert
        var savedCount = await _context.Trades.CountAsync(cancellationToken);
        savedCount.Should().Be(1000, "1000-ვე გარიგება წარმატებით უნდა შეინახოს ბაზაში");

        var firstTrade = await _context.Trades.FirstOrDefaultAsync(t => t.MakerOrderId == 1, cancellationToken);
        firstTrade.Should().NotBeNull();
        firstTrade!.Symbol.Should().Be("EURUSD");
    }

    [Fact]
    public async Task QueryTradesBySymbolAndTimestamp_ShouldSupportTimeSeriesPattern()
    {
        // Arrange
        var nowTicks = DateTime.UtcNow.Ticks;

        _context.Trades.AddRange(
            new TradeRecord
                { Symbol = "EURUSD", Timestamp = nowTicks, Price = 100, MakerOrderId = 1, TakerOrderId = 2 },
            new TradeRecord
                { Symbol = "EURUSD", Timestamp = nowTicks + 10, Price = 101, MakerOrderId = 3, TakerOrderId = 4 },
            new TradeRecord { Symbol = "GBPUSD", Timestamp = nowTicks, Price = 130, MakerOrderId = 5, TakerOrderId = 6 }
        );
        var cancellationToken = TestContext.Current.CancellationToken;
        await _context.SaveChangesAsync(cancellationToken);

        // Act: Time-Series მოთხოვნა (Symbol + Timestamp)
        var results = await _context.Trades
            .Where(t => t.Symbol == "EURUSD" && t.Timestamp >= nowTicks)
            .ToListAsync(cancellationToken);

        // Assert
        results.Should().HaveCount(2);
        results.All(t => t.Symbol == "EURUSD").Should().BeTrue();
    }
}
