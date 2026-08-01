using System.Diagnostics;
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

namespace FalconFX.Gateway.Tests.Performance;

public class GatewayParsingPerformanceTests
{
    [Fact]
    public void ProcessMessage_500ThousandMessages_ShouldParseUnder300Milliseconds()
    {
        // Arrange
        var redis = Substitute.For<IConnectionMultiplexer>();
        var hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
        var clientProxy = Substitute.For<IMarketClient>();
        var config = Substitute.For<IConfiguration>();

        hubContext.Clients.All.Returns(clientProxy);

        var subscriber = new RedisSubscriber(
            NullLogger<RedisSubscriber>.Instance,
            redis,
            hubContext,
            config
        );

        // 🚀 ოპტიმიზაცია: Reflection-ის MethodInfo გადავიყვანოთ Compiled Delegate-ში.
        // ეს სრულად აქრობს method.Invoke-ს და new object[]-ის ოვერჰედს ციკლში.
        var method = typeof(RedisSubscriber).GetMethod("ProcessMessage",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var processMessage = (Action<RedisValue>)Delegate.CreateDelegate(
            typeof(Action<RedisValue>), subscriber, method!);

        RedisValue payload = "EURUSD:10550";

        // Warmup (JIT კომპილაცია)
        processMessage(payload);

        var sw = Stopwatch.StartNew();

        // Act: 500,000 ტექსტის Span-პარსინგი პირდაპირი დელეგატით
        for (var i = 0; i < 500_000; i++) processMessage(payload);

        sw.Stop();

        // Assert: 500,000 შეტყობინება უნდა დამუშავდეს 300ms-ზე ნაკლებ დროში
        sw.ElapsedMilliseconds.Should().BeLessThan(300,
            $"500,000 ტექსტის Span-პარსინგს დასჭირდა {sw.ElapsedMilliseconds}ms");
    }
}