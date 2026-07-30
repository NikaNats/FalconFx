using FalconFX.Protos;
using Microsoft.EntityFrameworkCore;

namespace FalconFX.TradeProcessor.Data;

public class TradeDbContext(DbContextOptions<TradeDbContext> options) : DbContext(options)
{
    public DbSet<TradeRecord> Trades => Set<TradeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TradeRecord>()
            .HasIndex(t => new { t.Symbol, t.Timestamp });

        modelBuilder.Entity<TradeRecord>()
            .HasIndex(t => t.Timestamp);
    }
}

public class TradeRecord
{
    public long Id { get; set; }
    public long MakerOrderId { get; set; }
    public long TakerOrderId { get; set; }
    public long Price { get; set; }
    public long Quantity { get; set; }
    public long Timestamp { get; set; }
    public string Symbol { get; set; } = "";
    public DateTime InsertedAt { get; set; } = DateTime.UtcNow;

    public static TradeRecord FromProto(TradeExecuted proto)
    {
        return new TradeRecord
        {
            MakerOrderId = proto.MakerOrderId,
            TakerOrderId = proto.TakerOrderId,
            Price = proto.Price,
            Quantity = proto.Quantity,
            Symbol = proto.Symbol,
            Timestamp = proto.Timestamp
        };
    }
}