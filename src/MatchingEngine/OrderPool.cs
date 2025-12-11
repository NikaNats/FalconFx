using MatchingEngine.Models;

namespace MatchingEngine;

public class OrderPool
{
    private int _freeHead;
    private OrderNode[] _memory;

    public OrderPool(int size)
    {
        _memory = new OrderNode[size];
        Reset(); // ინიციალიზაცია
    }

    public int Rent()
    {
        // FIX: Don't throw Exception, let the caller handle it.
        if (_freeHead == -1) return -1; // Return invalid index to signal OOM

        var index = _freeHead;
        _freeHead = _memory[index].Next;

        // გასუფთავება (Next/Prev კავშირების გაწყვეტა)
        _memory[index].Next = -1;
        _memory[index].Prev = -1;

        return index;
    }

    public void Return(int index)
    {
        _memory[index].Next = _freeHead;
        _freeHead = index;
    }

    // ოპტიმიზებული Reset!
    public void Reset()
    {
        // 1. უბრალოდ ვანულებთ Free Pointer-ს საწყისზე
        _freeHead = 0;

        // 2. აღვადგენთ ჯაჭვს (O(N) - მაგრამ ეს არის Unsafe Memory Access-ის გარეშე ყველაზე საიმედო)
        // სისწრაფისთვის ვიყენებთ Span-ს, რომ არ შევამოწმოთ Bounds Check ყოველ ჯერზე
        // (თუმცა უბრალო for ციკლიც Ok არის)

        for (var i = 0; i < _memory.Length - 1; i++)
        {
            _memory[i].Next = i + 1;
            _memory[i].Prev = -1; // Optional
        }

        // ბოლო ელემენტი
        _memory[^1].Next = -1;
        _memory[^1].Prev = -1;

        // _maxUsedIndex-ს ვივიწყებთ დროებით, რადგან ის იყო ბაგის სავარაუდო წყარო
    }

    public ref OrderNode Get(int index)
    {
        return ref _memory[index];
    }
}