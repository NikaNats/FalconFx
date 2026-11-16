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

public class RedisSubscriberTests : IAsyncLifetime
{
    private readonly IMarketClient _clientProxy = Substitute.For<IMarketClient>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly CancellationTokenSource _cts = new();

    private readonly IHubContext<MarketHub, IMarketClient> _hubContext =
        Substitute.For<IHubContext<MarketHub, IMarketClient>>();

    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly RedisSubscriber _subscriber;
    private readonly ISubscriber _subscriberMock = Substitute.For<ISubscriber>();

    public RedisSubscriberTests()
    {
        _redis.GetSubscriber(Arg.Any<object>()).Returns(_subscriberMock);
        _hubContext.Clients.All.Returns(_clientProxy);

        _subscriber = new RedisSubscriber(
            NullLogger<RedisSubscriber>.Instance,
            _redis,
            _hubContext,
            _config
        );
    }

    public async ValueTask InitializeAsync()
    {
        // Start the background service so the SignalR broadcast channel loop is active
        await _subscriber.StartAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _subscriber.StopAsync(_cts.Token);
        _cts.Dispose();
    }

    [Theory]
    [InlineData("EURUSD:10550", "EURUSD", 10550)]
    [InlineData("GBPUSD:12700", "GBPUSD", 12700)]
    [InlineData("USDJPY:15520", "USDJPY", 15520)]
    [InlineData("BTCUSD:95000", "BTCUSD", 95000)]
    public async Task ProcessMessage_ValidPayload_ShouldBroadcastToSignalRClients(
        string rawMessage, string expectedSymbol, long expectedPrice)
    {
        // Deterministic completion: the broadcast callback signals instead of polling
        var broadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProxy.ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>())
            .Returns(_ =>
            {
                broadcast.TrySetResult();
                return Task.CompletedTask;
            });

        // Act
        _subscriber.ProcessMessage(rawMessage);

        await broadcast.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        await _clientProxy.Received(1).ReceiveMarketUpdate(expectedSymbol, expectedPrice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EURUSD")] // No colon
    [InlineData("EURUSD:INVALID")] // Non-numeric price
    [InlineData(":10000")] // Missing symbol
    [InlineData("EURUSD:10550abc")] // Trailing garbage must be rejected
    [InlineData("EURUSD:")] // Missing price
    public async Task ProcessMessage_MalformedPayload_ShouldHandleGracefullyWithoutException(string badMessage)
    {
        // Act
        var act = () => _subscriber.ProcessMessage(badMessage);

        // Assert: Should not throw exception
        act.Should().NotThrow();

        // Give the broadcast loop a bounded window to (incorrectly) deliver something
        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Nothing should be broadcasted to SignalR
        await _clientProxy.DidNotReceive().ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>());
    }

    [Fact]
    public async Task ProcessMessage_NullOrEmptyRedisValue_ShouldReturnEarlyWithoutProcessing()
    {
        // Act
        _subscriber.ProcessMessage(RedisValue.Null);
        _subscriber.ProcessMessage(RedisValue.EmptyString);

        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Assert
        await _clientProxy.DidNotReceive().ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>());
    }
}
