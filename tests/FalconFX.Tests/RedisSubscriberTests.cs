using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace FalconFX.Gateway.Tests;

public class RedisSubscriberTests
{
    [Fact]
    public async Task ProcessMessage_ValidPayload_ShouldBroadcastToSignalRClients()
    {
        // Arrange
        var logger = NullLogger<RedisSubscriber>.Instance;
        var redis = Substitute.For<IConnectionMultiplexer>();
        var hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
        var clientProxy = Substitute.For<IMarketClient>();
        var config = Substitute.For<IConfiguration>();

        hubContext.Clients.All.Returns(clientProxy);

        // Redis Payload: "EURUSD:10050"
        RedisValue redisMessage = "EURUSD:10050";

        // Subscriber-ის ინსტანსი
        var subscriber = new RedisSubscriber(logger, redis, hubContext, config);

        // Act - გამოვიძახოთ ProcessMessage (Private/Internal reflection-ით ან ტესტისთვის)
        var method = typeof(RedisSubscriber).GetMethod("ProcessMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method!.Invoke(subscriber, new object[] { redisMessage });

        // დაველოდოთ მცირე დრო ასინქრონულ Push-ს
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert - შევამოწმოთ, რომ SignalR Client-ს გადაეცა "EURUSD" და 10050
        await clientProxy.Received(1).ReceiveMarketUpdate("EURUSD", 10050);
    }
}