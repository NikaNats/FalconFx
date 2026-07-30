namespace FalconFX.MatchingEngine.Models;

/// <summary>
/// Intrusive Doubly-Linked List Node for zero-allocation OrderBook storage.
/// Uses array indices (Next, Prev) instead of heap-allocated C# object references.
/// </summary>
public struct OrderNode
{
    // ბიზნეს მონაცემები
    public long Id;
    public long Price;    // Scaled Price
    public long Quantity;
    public byte Side;     // 1 = Buy, 2 = Sell

    // Intrusive პოინტერები (ინდექსები მასივში)
    public int Next;      // -1 ნიშნავს სიის ბოლოს (NIL)
    public int Prev;      // -1 ნიშნავს სიის დასაწყისს (NIL)
}