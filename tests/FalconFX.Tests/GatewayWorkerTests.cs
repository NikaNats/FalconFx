using FalconFX.Gateway.Hubs;
using FalconFX.Gateway.Workers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace FalconFX.Tests;

public class GatewayWorkerTests
{
    [Fact]
    public async Task RedisMessage_Should_ParseAndBroadcast_ToSignalR()
    {
        // 1. Arrange
        var redis = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        var hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
        var clients = Substitute.For<IHubClients<IMarketClient>>();
        var clientProxy = Substitute.For<IMarketClient>();

        // Wire up the chain: Redis -> Subscriber
        redis.GetSubscriber().Returns(subscriber);

        // Wire up the chain: Hub -> Clients -> All -> ClientProxy
        hubContext.Clients.Returns(clients);
        clients.All.Returns(clientProxy);

        // Capture the internal callback that the worker registers
        Action<RedisChannel, RedisValue> capturedHandler = null!;

        // Set up the mock to capture the handler when SubscribeAsync is called
        subscriber.SubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Do<Action<RedisChannel, RedisValue>>(handler => capturedHandler = handler),
            Arg.Any<CommandFlags>()
        ).Returns(Task.CompletedTask);

        var worker = new RedisSubscriber(
            NullLogger<RedisSubscriber>.Instance,
            redis,
            hubContext
        );

        // 2. Act
        // Start the worker (this triggers the SubscribeAsync call we mocked above)
        var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for subscription to be set up
        await Task.Delay(100);

        Assert.NotNull(capturedHandler); // Ensure subscription happened

        // MANUALLY simulate a Redis message arriving
        capturedHandler(RedisChannel.Literal("market_updates"), "EURUSD:10550");

        // Give the Fire-and-Forget thread a moment to execute
        await Task.Delay(50);

        // Stop the worker
        cts.Cancel();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        // 3. Assert
        // Verify the parsed data was sent to the interface
        await clientProxy.Received(1).ReceiveMarketUpdate("EURUSD", 10550);
    }

    [Fact]
    public async Task BadMessage_Should_Not_Crash_Worker()
    {
        // 1. Arrange
        var redis = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        var hubContext = Substitute.For<IHubContext<MarketHub, IMarketClient>>();
        var clients = Substitute.For<IHubClients<IMarketClient>>();
        var clientProxy = Substitute.For<IMarketClient>();

        redis.GetSubscriber().Returns(subscriber);
        // Wire up the chain even though we expect no call
        hubContext.Clients.Returns(clients);
        clients.All.Returns(clientProxy);

        Action<RedisChannel, RedisValue> capturedHandler = null!;
        subscriber.SubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Do<Action<RedisChannel, RedisValue>>(h => capturedHandler = h),
            Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);

        var worker = new RedisSubscriber(NullLogger<RedisSubscriber>.Instance, redis, hubContext);

        // 2. Act
        var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for subscription
        await Task.Delay(100);

        Assert.NotNull(capturedHandler);

        // Send garbage data
        capturedHandler(RedisChannel.Literal("market_updates"), "GARBAGE_DATA_NO_COLON");

        await Task.Delay(50);

        // Stop the worker
        cts.Cancel();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        // 3. Assert
        // Verify we did NOT send anything (and implicitly, that we didn't crash)
        await clientProxy.DidNotReceive().ReceiveMarketUpdate(Arg.Any<string>(), Arg.Any<long>());
    }
}