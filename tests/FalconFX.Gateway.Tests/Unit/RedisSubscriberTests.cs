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

public class RedisSubscriberTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IHubContext<MarketHub, IMarketClient> _hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
    private readonly IMarketClient _clientProxy = Substitute.For<IMarketClient>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly RedisSubscriber _subscriber;

    public RedisSubscriberTests()
    {
        _hubContext.Clients.All.Returns(_clientProxy);

        _subscriber = new RedisSubscriber(
            NullLogger<RedisSubscriber>.Instance,
            _redis,
            _hubContext,
            _config
        );
    }

    [Theory]
    [InlineData("EURUSD:10550", "EURUSD", 10550)]
    [InlineData("GBPUSD:12700", "GBPUSD", 12700)]
    [InlineData("USDJPY:15520", "USDJPY", 15520)]
    [InlineData("BTCUSD:95000", "BTCUSD", 95000)]
    public async Task ProcessMessage_ValidPayload_ShouldBroadcastToSignalRClients(
        string rawMessage, string expectedSymbol, long expectedPrice)
    {
        // Act
        InvokeProcessMessage(rawMessage);

        await Task.Delay(50);

        // Assert
        await _clientProxy.Received(1).ReceiveMarketUpdate(expectedSymbol, expectedPrice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EURUSD")]             // ორი წერტილის გარეშე
    [InlineData("EURUSD:INVALID")]     // ფასი არ არის ციფრი
    [InlineData(":10000")]             // აკლია სიმბოლო
    [InlineData("EURUSD:10550:EXTRA")] // ზედმეტი სეგმენტები
    public async Task ProcessMessage_MalformedPayload_ShouldHandleGracefullyWithoutException(string badMessage)
    {
        // Act
        var act = () => InvokeProcessMessage(badMessage);

        // Assert: არ უნდა ისროლოს Exception
        act.Should().NotThrow();

        await Task.Delay(50);

        // SignalR-ში არაფერი არ უნდა გაიგზავნოს
        await _clientProxy.DidNotReceive().ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>());
    }

    [Fact]
    public async Task ProcessMessage_NullOrEmptyRedisValue_ShouldReturnEarlyWithoutProcessing()
    {
        // Act
        InvokeProcessMessage(RedisValue.Null);
        InvokeProcessMessage(RedisValue.EmptyString);

        await Task.Delay(50);

        // Assert
        await _clientProxy.DidNotReceive().ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>());
    }

    private void InvokeProcessMessage(RedisValue message)
    {
        var method = typeof(RedisSubscriber).GetMethod("ProcessMessage",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method!.Invoke(_subscriber, new object[] { message });
    }
}