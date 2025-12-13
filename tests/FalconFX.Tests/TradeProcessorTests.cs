using FalconFX.Protos;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TradeDbContext = FalconFX.TradeProcessor.Data.TradeDbContext;
using TradeRecord = FalconFX.TradeProcessor.Data.TradeRecord;

// Namespace for the processor logic
namespace FalconFX.Tests;

public class TradeMappingTests
{
    [Fact]
    public async Task TradeRecord_Should_Persist_With_Correct_Timestamp()
    {
        // Arrange: In-Memory DB for logic check (Use Testcontainers for real SQL tests)
        var options = new DbContextOptionsBuilder<TradeDbContext>()
            .UseInMemoryDatabase("TradesDb_" + Guid.NewGuid())
            .Options;

        await using var db = new TradeDbContext(options);

        var tradeProto = new TradeExecuted
        {
            Id = 101,
            Symbol = "EURUSD",
            Price = 10550,
            Quantity = 100,
            MakerOrderId = 1,
            TakerOrderId = 2,
            Timestamp = DateTime.UtcNow.Ticks
        };

        var record = TradeRecord.FromProto(tradeProto);

        db.Trades.Add(record);
        await db.SaveChangesAsync();

        // Assert
        var saved = await db.Trades.FirstOrDefaultAsync();
        Assert.NotNull(saved);
        Assert.Equal("EURUSD", saved.Symbol);
        Assert.Equal(10550, saved.Price);
        // Ensure index timestamp logic holds
        Assert.True(saved.InsertedAt <= DateTime.UtcNow);
    }
}