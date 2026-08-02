using FalconFX.MatchingEngine.Models;
using FalconFX.MatchingEngine.Services;
using FluentAssertions;
using Xunit;

namespace FalconFX.MatchingEngine.Tests.Unit;

public class PreTradeRiskCheckerTests
{
    private readonly PreTradeRiskChecker _riskChecker = new(
        maxOrderQuantity: 100,
        maxNotionalValue: 10_000,
        maxPriceDeviation: 10);

    [Fact]
    public void ValidateOrder_ValidOrder_ShouldReturnPassed()
    {
        var order = new Order(1, OrderSide.Buy, 100, 10); // Notional = 1000
        var result = _riskChecker.ValidateOrder(in order, currentMarketPrice: 100);

        result.Should().Be(RiskCheckResult.Passed);
    }

    [Fact]
    public void ValidateOrder_ExceedingQuantity_ShouldReturnRejected()
    {
        var order = new Order(1, OrderSide.Buy, 100, 150); // Quantity 150 > 100
        var result = _riskChecker.ValidateOrder(in order, currentMarketPrice: 100);

        result.Should().Be(RiskCheckResult.Rejected_MaxOrderQuantityExceeded);
    }

    [Fact]
    public void ValidateOrder_ExceedingNotionalValue_ShouldReturnRejected()
    {
        var order = new Order(1, OrderSide.Buy, 200, 80); // Notional = 16,000 > 10,000
        var result = _riskChecker.ValidateOrder(in order, currentMarketPrice: 200);

        result.Should().Be(RiskCheckResult.Rejected_MaxNotionalValueExceeded);
    }

    [Fact]
    public void ValidateOrder_PriceDeviationExceeded_ShouldReturnFatFingerRejected()
    {
        var order = new Order(1, OrderSide.Buy, 150, 10); // Market price = 100, Dev = 50 > 10
        var result = _riskChecker.ValidateOrder(in order, currentMarketPrice: 100);

        result.Should().Be(RiskCheckResult.Rejected_FatFingerPriceDeviation);
    }
}