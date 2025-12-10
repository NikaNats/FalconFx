namespace MatchingEngine.Models;

public class Order
{
    public Order(long id, OrderSide side, decimal price, decimal quantity)
    {
        Id = id;
        Side = side;
        Price = price;
        OriginalQuantity = quantity;
        RemainingQuantity = quantity;
    }

    public long Id { get; init; } // Unique ID (Snowflake or simple long)
    public OrderSide Side { get; init; }
    public decimal Price { get; init; }
    public decimal OriginalQuantity { get; init; }

    // ეს ველი შეიცვლება მუშაობის პროცესში
    public decimal RemainingQuantity { get; set; }
}