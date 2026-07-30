using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FalconFX.MatchingEngine.Tests.Unit;

public class EngineWorkerTests
{
    [Fact]
    public async Task EngineWorker_ShouldProcessOrders_AndPublishTradesToKafka()
    {
        // Arrange
        var kafkaProducer = Substitute.For<IProducer<Null, byte[]>>();
        var logger = NullLogger<EngineWorker>.Instance;
        var engineWorker = new EngineWorker(logger, kafkaProducer);

        using var cts = new CancellationTokenSource();

        // Act
        await engineWorker.StartAsync(cts.Token);

        // Enqueue 2 matching orders
        engineWorker.EnqueueOrder(new Order(1, OrderSide.Sell, 100, 10));
        engineWorker.EnqueueOrder(new Order(2, OrderSide.Buy, 100, 10));

        // Allow async channels to drain
        await Task.Delay(150);

        await engineWorker.StopAsync(cts.Token);

        // Assert: Check Kafka Producer invocation
        kafkaProducer.Received(1).Produce(
            Arg.Is<string>("trades"),
            Arg.Is<Message<Null, byte[]>>(msg => msg.Value != null && msg.Value.Length > 0)
        );
    }
}