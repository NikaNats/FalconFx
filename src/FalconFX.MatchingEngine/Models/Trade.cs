namespace FalconFX.MatchingEngine.Models;

// გადავაკეთეთ struct-ად
public readonly struct Trade(long price, long quantity, long makerId, long takerId, long timestamp = 0)
{
    public long Price { get; } = price;
    public long Quantity { get; } = quantity;
    public long MakerOrderId { get; } = makerId;
    public long TakerOrderId { get; } = takerId;
    public long Timestamp { get; } = timestamp == 0 ? DateTime.UtcNow.Ticks : timestamp; // DateTime-ის მაგივრად long (Ticks) უფრო სწრაფია
}