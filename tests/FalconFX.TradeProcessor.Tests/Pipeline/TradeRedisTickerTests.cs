using FalconFX.Protos;
using FluentAssertions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace FalconFX.TradeProcessor.Tests.Pipeline;

public class TradeRedisTickerTests
{
    [Fact]
    public async Task RedisMarketUpdate_ShouldFormatKeyAndPayloadCorrectly()
    {
        // Arrange
        var redisDb = Substitute.For<IDatabase>();
        var trade = new TradeExecuted
        {
            Symbol = "EURUSD",
            Price = 10550,
            Quantity = 100
        };

        // Act: Span-ზე დაფუძნებული სტრინგის ფორმატირება
        Span<char> span = stackalloc char[trade.Symbol.Length + 1 + 20];
        trade.Symbol.AsSpan().CopyTo(span);
        span[trade.Symbol.Length] = ':';

        trade.Price.TryFormat(span[(trade.Symbol.Length + 1)..], out var charsWritten);
        var redisPayload = span[..(trade.Symbol.Length + 1 + charsWritten)].ToString();

        await redisDb.StringSetAsync($"ticker:{trade.Symbol}", trade.Price, flags: CommandFlags.FireAndForget);
        await redisDb.PublishAsync(RedisChannel.Literal("market_updates"), redisPayload, CommandFlags.FireAndForget);

        // Assert
        redisPayload.Should().Be("EURUSD:10550");

        await redisDb.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "ticker:EURUSD"),
            Arg.Is<RedisValue>(v => (long)v == 10550),
            flags: CommandFlags.FireAndForget
        );

        await redisDb.Received(1).PublishAsync(
            Arg.Is<RedisChannel>(c => c.ToString() == "market_updates"),
            Arg.Is<RedisValue>(v => v.ToString() == "EURUSD:10550"),
            flags: CommandFlags.FireAndForget
        );
    }
}