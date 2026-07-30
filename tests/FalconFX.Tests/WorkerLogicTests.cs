using FalconFX.MarketMaker;
using FalconFX.Protos;
using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace FalconFX.Tests;

public class WorkerLogicTests
{
    [Fact]
    public void XorShift64_Next_ShouldAlwaysProduceNumbersWithinRequestedRange()
    {
        var rng = new XorShift64(12345);
        const int minInclusive = 99;
        const int maxExclusive = 102;

        for (var i = 0; i < 100_000; i++)
        {
            var value = rng.Next(minInclusive, maxExclusive);
            value.Should().BeInRange(99, 101);
        }
    }

    [Fact]
    public void ProtobufSerialization_WriteToSpan_ShouldDeserializeCorrectly()
    {
        var request = new SubmitOrderRequest
        {
            Id = 5001,
            Side = 1,
            Price = 100,
            Quantity = 10
        };

        var msgSize = request.CalculateSize();
        var buffer = new byte[msgSize];

        request.WriteTo(buffer.AsSpan(0, msgSize));

        var parsedRequest = SubmitOrderRequest.Parser.ParseFrom(buffer);

        parsedRequest.Id.Should().Be(5001);
        parsedRequest.Side.Should().Be(1);
        parsedRequest.Price.Should().Be(100);
        parsedRequest.Quantity.Should().Be(10);
    }
}