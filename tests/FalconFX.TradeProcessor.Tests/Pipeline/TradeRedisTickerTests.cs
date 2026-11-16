using FalconFX.TradeProcessor;
using FluentAssertions;
using Xunit;

namespace FalconFX.TradeProcessor.Tests.Pipeline;

public class TradeRedisTickerTests
{
    [Theory]
    [InlineData("EURUSD", 10550, "EURUSD:10550")]
    [InlineData("GBPUSD", -1, "GBPUSD:-1")]
    [InlineData("BTCUSD", long.MaxValue, "BTCUSD:9223372036854775807")]
    [InlineData("X", 0, "X:0")]
    public void FormatRedisPayload_ShouldProduceSymbolColonPrice(
        string symbol, long price, string expected)
    {
        // Act
        var payload = Worker.FormatRedisPayload(symbol, price);

        // Assert
        payload.Should().Be(expected);
    }

    [Fact]
    public void FormatRedisPayload_LongSymbol_ShouldNotThrow()
    {
        // Arrange: a symbol far longer than the old fixed stackalloc buffer
        var symbol = new string('A', 4096);

        // Act
        var act = () => Worker.FormatRedisPayload(symbol, 100);

        // Assert
        act.Should().NotThrow();
        act().Should().Be($"{symbol}:100");
    }
}
