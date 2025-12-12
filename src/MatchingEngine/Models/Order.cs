namespace MatchingEngine.Models;

public struct Order(long id, OrderSide side, decimal price, decimal quantity)
{
    public long Id { get; init; } = id; // Unique ID (Snowflake or simple long)
    public OrderSide Side { get; init; } = side;
    public decimal Price { get; init; } = price;
    public decimal OriginalQuantity { get; init; } = quantity;

    // ეს ველი შეიცვლება მუშაობის პროცესში
    public decimal RemainingQuantity { get; set; } = quantity;
}