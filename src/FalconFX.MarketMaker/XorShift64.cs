using System.Runtime.CompilerServices;

namespace FalconFX.MarketMaker;

/// <summary>
///     Sub-nanosecond, zero-allocation Pseudo-Random Number Generator (XorShift64).
///     Replaces Random.Shared to eliminate lock contention and boxing.
/// </summary>
public struct XorShift64
{
    private ulong _state;

    public XorShift64(ulong seed)
    {
        _state = seed == 0 ? 0x853c49e6748fea9bUL : seed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextUint64()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        return _state = x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Next(int minInclusive, int maxExclusive)
    {
        var range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUint64() % range);
    }
}