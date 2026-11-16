using System.Collections;
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

        // Act: pipe EURUSD through 1000 times
        for (var i = 0; i < 1000; i++) subscriber.ProcessMessage("EURUSD:10000");

        // Assert: SymbolPool must contain a single entry
        var symbolPoolField = typeof(RedisSubscriber).GetField("SymbolPool",
            BindingFlags.NonPublic | BindingFlags.Static);

        var poolDict = symbolPoolField!.GetValue(null) as IDictionary;

        poolDict.Should().NotBeNull();
        poolDict!.Contains("EURUSD").Should().BeTrue();
        poolDict.Count.Should().BeGreaterThanOrEqualTo(1);
    }
}
