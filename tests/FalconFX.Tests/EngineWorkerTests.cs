using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FalconFX.MatchingEngine.Tests;

public class EngineWorkerTests
{
    [Fact]
    public async Task EngineWorker_EnqueueOrders_ShouldProcessOrdersAndCreateMatches()
    {
        // Arrange
        var logger = NullLogger<EngineWorker>.Instance;
        var producer = Substitute.For<IProducer<Null, byte[]>>(); // Mock Kafka Producer

        var engine = new EngineWorker(logger, producer);
        using var cts = new CancellationTokenSource();

        // Act - გავუშვათ Engine
        var engineTask = engine.StartAsync(cts.Token);

        // გავაგზავნოთ 2 დამთხვევადი ორდერი Enqueue-ით
        var sell = new Order(1, OrderSide.Sell, 100, 10);
        var buy = new Order(2, OrderSide.Buy, 100, 10);

        engine.EnqueueOrder(sell);
        engine.EnqueueOrder(buy);

        // დაველოდოთ მცირე დრო დამუშავებისთვის
        await Task.Delay(200);

        // გავაჩეროთ Engine
        await cts.CancelAsync();
        await engineTask;

        // Assert - შევამოწმოთ, რომ Kafka Producer-ს გადაეცა გარიგების შეტყობინება
        producer.Received(1).Produce(
            Arg.Is<string>(topic => topic == "trades"),
            Arg.Is<Message<Null, byte[]>>(msg => msg.Value != null)
        );
    }
}