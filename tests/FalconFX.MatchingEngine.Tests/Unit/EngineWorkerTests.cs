using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FalconFX.MatchingEngine.Tests.Unit;

public class EngineWorkerTests
{
    [Fact]
    public async Task EngineWorker_ShouldProcessOrders_AndPublishTradesToKafka()
    {
        // Arrange: Mock the typed Kafka producer required by EngineWorker
        var kafkaProducer = Substitute.For<IProducer<long, TradeExecuted>>();
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

        // Assert: Verify Kafka Producer invocation with typed TradeExecuted message
        kafkaProducer.Received(1).Produce(
            Arg.Is<string>("trades"),
            Arg.Is<Message<long, TradeExecuted>>(msg =>
                msg.Value != null &&
                msg.Value.Price == 100 &&
                msg.Value.Quantity == 10 &&
                msg.Value.MakerOrderId == 1 &&
                msg.Value.TakerOrderId == 2)
        );
    }
}