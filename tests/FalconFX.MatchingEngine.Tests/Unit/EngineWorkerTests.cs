using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FluentAssertions;
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
        // Deterministic ID for the assertion below; production seeds from the clock.
        var engineWorker = new EngineWorker(logger, kafkaProducer, initialTradeId: 0);

        // Deterministic completion instead of an arbitrary Task.Delay
        var tradeProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kafkaProducer
            .When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<long, TradeExecuted>>(),
                Arg.Any<Action<DeliveryReport<long, TradeExecuted>>>()))
            .Do(_ => tradeProduced.TrySetResult());

        using var cts = new CancellationTokenSource();

        // Act
        await engineWorker.StartAsync(cts.Token);

        // Enqueue 2 matching orders
        engineWorker.EnqueueOrder(new Order(1, OrderSide.Sell, 100, 10));
        engineWorker.EnqueueOrder(new Order(2, OrderSide.Buy, 100, 10));

        await tradeProduced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await engineWorker.StopAsync(cts.Token);

        // Assert: Verify Kafka Producer invocation with typed TradeExecuted message
        kafkaProducer.Received(1).Produce(
            Arg.Is("trades"),
            Arg.Is<Message<long, TradeExecuted>>(msg =>
                msg.Value != null &&
                msg.Value.Price == 100 &&
                msg.Value.Quantity == 10 &&
                msg.Value.MakerOrderId == 1 &&
                msg.Value.TakerOrderId == 2 &&
                msg.Value.Id == 1),
            Arg.Any<Action<DeliveryReport<long, TradeExecuted>>>()
        );
    }

    [Fact]
    public async Task EngineWorker_ShouldSeedTradeIdsFromClock_SoRestartsDoNotReusePersistedIds()
    {
        // Without a time-based seed a restarted engine would reuse TradeId 1, 2, 3... and
        // PostgreSQL's ON CONFLICT ("TradeId") DO NOTHING would silently drop every trade.
        var kafkaProducer = Substitute.For<IProducer<long, TradeExecuted>>();
        var engineWorker = new EngineWorker(NullLogger<EngineWorker>.Instance, kafkaProducer);

        var producedTradeId = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        kafkaProducer
            .When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<long, TradeExecuted>>(),
                Arg.Any<Action<DeliveryReport<long, TradeExecuted>>>()))
            .Do(call => producedTradeId.TrySetResult(
                call.Arg<Message<long, TradeExecuted>>().Value.Id));

        using var cts = new CancellationTokenSource();

        await engineWorker.StartAsync(cts.Token);
        engineWorker.EnqueueOrder(new Order(1, OrderSide.Sell, 100, 10));
        engineWorker.EnqueueOrder(new Order(2, OrderSide.Buy, 100, 10));

        var tradeId = await producedTradeId.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await engineWorker.StopAsync(cts.Token);

        // Seed = Unix milliseconds * 1_000_000, which exceeds 1e18 for any date after 2001.
        tradeId.Should().BeGreaterThan(1_000_000_000_000_000_000L);
    }

    [Fact]
    public async Task EngineWorker_ShouldReleaseSkippedOffset_InFifoOrderBehindEarlierOrders()
    {
        // A poison-pill skip must not be stored ahead of orders that are still queued,
        // otherwise a crash can commit offsets past trades that were never produced.
        var kafkaProducer = Substitute.For<IProducer<long, TradeExecuted>>();
        var engineWorker = new EngineWorker(
            NullLogger<EngineWorker>.Instance, kafkaProducer, initialTradeId: 0);

        using var cts = new CancellationTokenSource();
        await engineWorker.StartAsync(cts.Token);

        var partition = new TopicPartition("orders", 0);
        engineWorker.TryEnqueueOrder(new OrderEnvelope(
            new Order(1, OrderSide.Sell, 100, 10), new TopicPartitionOffset(partition, 0)));
        engineWorker.TryEnqueueOrder(OrderEnvelope.Skip(new TopicPartitionOffset(partition, 1)));
        engineWorker.TryEnqueueOrder(new OrderEnvelope(
            new Order(2, OrderSide.Buy, 100, 10), new TopicPartitionOffset(partition, 2)));

        var offsets = new List<long>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (offsets.Count < 3 && DateTime.UtcNow < deadline)
        {
            if (engineWorker.TryDequeueProcessedOffset(out var offset))
                offsets.Add(offset.Offset.Value);
            else
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await engineWorker.StopAsync(cts.Token);

        offsets.Should().Equal(0L, 1L, 2L);
    }
}
