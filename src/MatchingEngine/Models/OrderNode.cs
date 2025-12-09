namespace MatchingEngine.Models;

public struct OrderNode
{
    // ბიზნეს მონაცემები
    public long Id;
    public long Price; // უკვე გამრავლებული (Scaled)
    public long Quantity;
    public byte Side; // 1 = Buy, 2 = Sell

    // SOTA ნაწილი: "Intrusive" პოინტერები
    // ჩვენ არ ვიყენებთ OrderNode-ს რეფერენსებს, ვიყენებთ ინდექსებს მასივში
    public int Next; 
    public int Prev;
    
    // -1 ნიშნავს რომ არავინაა
}