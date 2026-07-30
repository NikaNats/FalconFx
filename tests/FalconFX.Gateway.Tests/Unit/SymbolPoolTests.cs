using System.Reflection;
using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace FalconFX.Gateway.Tests.Unit;

public class SymbolPoolTests
{
    [Fact]
    public void SymbolPool_ShouldReuseSameStringInstance_ToPreventGCAllocations()
    {
        // Arrange
        var redis = Substitute.For<IConnectionMultiplexer>();
        var hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
        var config = Substitute.For<IConfiguration>();

        var subscriber = new RedisSubscriber(
            NullLogger<RedisSubscriber>.Instance,
            redis,
            hubContext,
            config
        );

        var method = typeof(RedisSubscriber).GetMethod("ProcessMessage",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Act: 1000-ჯერ გავატაროთ EURUSD
        for (int i = 0; i < 1000; i++)
        {
            method!.Invoke(subscriber, new object[] { (RedisValue)"EURUSD:10000" });
        }

        // Assert: SymbolPool-ში უნდა იყოს მხოლოდ 1 ჩანაწერი
        var symbolPoolField = typeof(RedisSubscriber).GetField("SymbolPool",
            BindingFlags.NonPublic | BindingFlags.Static);

        var poolDict = symbolPoolField!.GetValue(null) as System.Collections.IDictionary;

        poolDict.Should().NotBeNull();
        poolDict!.Contains("EURUSD").Should().BeTrue();
        poolDict.Count.Should().BeGreaterThanOrEqualTo(1);
    }
}