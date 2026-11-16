using System.Runtime.CompilerServices;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine.Services;

public enum RiskCheckResult : byte
{
    Passed = 0,
    Rejected_MaxOrderQuantityExceeded = 1,
    Rejected_MaxNotionalValueExceeded = 2,
    Rejected_FatFingerPriceDeviation = 3,
    Rejected_InvalidOrderParameters = 4
}

/// <summary>
///     Ultra-low latency (< 1 microsecond) Pre-Trade Risk Engine.
///     Operates completely on stack and inline memory without heap allocations.
///     Arithmetic is performed in <see cref="Int128" /> so extreme prices/quantities
///     can never overflow or wrap around a check.
/// </summary>
public readonly struct PreTradeRiskChecker
{
    private readonly Int128 _maxNotionalValue;
    private readonly long _maxOrderQuantity;
    private readonly long _maxPriceDeviation;

    public PreTradeRiskChecker(long maxOrderQuantity, long maxNotionalValue, long maxPriceDeviation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOrderQuantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNotionalValue);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPriceDeviation);

        _maxOrderQuantity = maxOrderQuantity;
        _maxNotionalValue = maxNotionalValue;
        _maxPriceDeviation = maxPriceDeviation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RiskCheckResult ValidateOrder(in Order order, long currentMarketPrice)
    {
        // 1. Structural & Domain Validation
        if (order.OriginalQuantity <= 0 ||
            order.Price <= 0 ||
            (order.Side != OrderSide.Buy && order.Side != OrderSide.Sell))
            return RiskCheckResult.Rejected_InvalidOrderParameters;

        // 2. Max Order Quantity Check
        if (order.OriginalQuantity > _maxOrderQuantity)
            return RiskCheckResult.Rejected_MaxOrderQuantityExceeded;

        // 3. Overflow-free Notional Value Check using Int128
        Int128 notional = (Int128)order.Price * order.OriginalQuantity;
        if (notional > _maxNotionalValue)
            return RiskCheckResult.Rejected_MaxNotionalValueExceeded;

        // 4. Overflow-free Fat-Finger Price Check
        Int128 diff = (Int128)order.Price - currentMarketPrice;
        if (diff < 0) diff = -diff;

        if (diff > _maxPriceDeviation)
            return RiskCheckResult.Rejected_FatFingerPriceDeviation;

        return RiskCheckResult.Passed;
    }
}
