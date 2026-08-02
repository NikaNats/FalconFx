using System.Runtime.CompilerServices;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine.Services;

public enum RiskCheckResult : byte
{
    Passed = 0,
    Rejected_MaxOrderQuantityExceeded = 1,
    Rejected_MaxNotionalValueExceeded = 2,
    Rejected_FatFingerPriceDeviation = 3
}

/// <summary>
/// Ultra-low latency (< 1 microsecond) Pre-Trade Risk Engine.
/// Operates completely on stack and inline memory without heap allocations.
/// </summary>
public struct PreTradeRiskChecker
{
    private readonly long _maxOrderQuantity;
    private readonly long _maxNotionalValue;
    private readonly long _maxPriceDeviation;

    public PreTradeRiskChecker(long maxOrderQuantity, long maxNotionalValue, long maxPriceDeviation)
    {
        _maxOrderQuantity = maxOrderQuantity;
        _maxNotionalValue = maxNotionalValue;
        _maxPriceDeviation = maxPriceDeviation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RiskCheckResult ValidateOrder(in Order order, long currentMarketPrice)
    {
        // 1. Max Order Quantity Check
        if (order.OriginalQuantity > _maxOrderQuantity)
            return RiskCheckResult.Rejected_MaxOrderQuantityExceeded;

        // 2. Max Notional Value Check (Price * Quantity)
        if (order.Price * order.OriginalQuantity > _maxNotionalValue)
            return RiskCheckResult.Rejected_MaxNotionalValueExceeded;

        // 3. Fat-Finger Price Check (e.g., price deviates too much from current market price)
        var priceDiff = Math.Abs(order.Price - currentMarketPrice);
        if (priceDiff > _maxPriceDeviation)
            return RiskCheckResult.Rejected_FatFingerPriceDeviation;

        return RiskCheckResult.Passed;
    }
}