namespace MatchingEngine.Models;

// გადავაკეთეთ struct-ად
public readonly struct Trade
{
    public decimal Price { get; }
    public decimal Quantity { get; }
    public long MakerOrderId { get; }
    public long TakerOrderId { get; }
    public long Timestamp { get; } // DateTime-ის მაგივრად long (Ticks) უფრო სწრაფია

    public Trade(decimal price, decimal quantity, long makerId, long takerId)
    {
        Price = price;
        Quantity = quantity;
        MakerOrderId = makerId;
        TakerOrderId = takerId;
        Timestamp = DateTime.UtcNow.Ticks;
    }
}