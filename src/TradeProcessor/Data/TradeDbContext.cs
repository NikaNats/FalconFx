using Microsoft.EntityFrameworkCore;

public class TradeDbContext(DbContextOptions<TradeDbContext> options) : DbContext(options)
{
    public DbSet<TradeRecord> Trades => Set<TradeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Optimize for Time-Series queries
        modelBuilder.Entity<TradeRecord>()
            .HasIndex(t => t.Timestamp);

        modelBuilder.Entity<TradeRecord>()
            .HasIndex(t => t.Symbol);
    }
}

public class TradeRecord
{
    public long Id { get; set; } // Primary Key
    public long MakerOrderId { get; set; }
    public long TakerOrderId { get; set; }
    public long Price { get; set; }
    public long Quantity { get; set; }
    public long Timestamp { get; set; }
    public string Symbol { get; set; } = "";
    public DateTime InsertedAt { get; set; } = DateTime.UtcNow;
}