namespace FalconFX.MatchingEngine.Models;

// გადავაკეთეთ struct-ად
public readonly struct Trade(decimal price, decimal quantity, long makerId, long takerId)
{
    public decimal Price { get; } = price;
    public decimal Quantity { get; } = quantity;
    public long MakerOrderId { get; } = makerId;
    public long TakerOrderId { get; } = takerId;
    public long Timestamp { get; } = DateTime.UtcNow.Ticks; // DateTime-ის მაგივრად long (Ticks) უფრო სწრაფია
}