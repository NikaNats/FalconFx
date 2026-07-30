namespace FalconFX.MatchingEngine.Models;

public struct Order(long id, OrderSide side, long price, long quantity)
{
    public long Id { get; init; } = id; // Unique ID (Snowflake or simple long)
    public OrderSide Side { get; init; } = side;
    public long Price { get; init; } = price;
    public long OriginalQuantity { get; init; } = quantity;

    // ეს ველი შეიცვლება მუშაობის პროცესში
    public long RemainingQuantity { get; set; } = quantity;
}