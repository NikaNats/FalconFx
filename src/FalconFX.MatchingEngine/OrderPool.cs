using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine;

public sealed class OrderPool
{
    private readonly OrderNode[] _memory;
    private int _freeHead;

    public OrderPool(int size)
    {
        _memory = new OrderNode[size];
        Reset();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Rent()
    {
        if (_freeHead == -1) return -1; // Out of Memory, OrderBook-მა უნდა დაამუშაოს

        var index = _freeHead;

        // Zero-cost bounds check bypass using MemoryMarshal
        ref var node = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), index);
        _freeHead = node.Next;

        node.Next = -1;
        node.Prev = -1;

        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(int index)
    {
        ref var node = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), index);
        node.Next = _freeHead;
        _freeHead = index;
    }

    public void Reset()
    {
        _freeHead = 0;
        var span = _memory.AsSpan(); // Span-ის გამოყენება სწრაფი ინიციალიზაციისთვის

        for (var i = 0; i < span.Length - 1; i++)
        {
            span[i].Next = i + 1;
            span[i].Prev = -1;
        }

        span[^1].Next = -1;
        span[^1].Prev = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref OrderNode Get(int index)
    {
        // ბევრად უფრო სწრაფი ვიდრე _memory[index], რადგან არ ამოწმებს საზღვრებს ყოველ ჯერზე
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), index);
    }
}