using FalconFX.Protos;
using FalconFX.TradeProcessor.Data;
using FluentAssertions;
using Xunit;

namespace FalconFX.TradeProcessor.Tests.Data;

public class TradeRecordMappingTests
{
    [Fact]
    public void FromProto_ShouldMapAllFieldsCorrectly_WithoutDataLoss()
    {
        // Arrange
        var protoTrade = new TradeExecuted
        {
            Id = 999,
            MakerOrderId = 1001,
            TakerOrderId = 2002,
            Price = 10050,
            Quantity = 10,
            Symbol = "EURUSD",
            Timestamp = DateTime.UtcNow.Ticks
        };

        // Act
        var record = TradeRecord.FromProto(protoTrade);

        // Assert
        record.Should().NotBeNull();
        record.MakerOrderId.Should().Be(1001);
        record.TakerOrderId.Should().Be(2002);
        record.Price.Should().Be(10050);
        record.Quantity.Should().Be(10);
        record.Symbol.Should().Be("EURUSD");
        record.Timestamp.Should().Be(protoTrade.Timestamp);
        record.InsertedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }
}