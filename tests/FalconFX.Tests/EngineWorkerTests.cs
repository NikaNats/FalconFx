using Confluent.Kafka;
using MatchingEngine;
using MatchingEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FalconFX.Tests;

public class EngineWorkerTests
{
    [Fact]
    public async Task Process_Should_ReadFromChannel_And_ProduceToKafka()
    {
        // 1. Arrange
        var producer = Substitute.For<IProducer<string, byte[]>>();
        var logger = NullLogger<EngineWorker>.Instance;

        // We use the REAL EngineWorker logic, but mocking the external Kafka dependency
        var worker = new EngineWorker(logger, producer);

        var cts = new CancellationTokenSource();

        // 2. Act
        // Start the background thread
        var workerTask = worker.StartAsync(cts.Token);

        // Inject 2 crossing orders directly into the worker
        // Sell 10 @ 100
        worker.EnqueueOrder(new Order(1, OrderSide.Sell, 100, 10));
        // Buy 10 @ 100 (Matches immediately)
        worker.EnqueueOrder(new Order(2, OrderSide.Buy, 100, 10));

        // Allow the async loop to process (HFT is fast, 200ms is plenty)
        await Task.Delay(200);

        // Stop the worker gracefully
        cts.Cancel();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        // 3. Assert
        // Verify Producer.Produce was called exactly once (for the 1 trade generated)
        producer.Received(1).Produce(
            "trades",
            // Validate the message value is not null/empty
            Arg.Is<Message<string, byte[]>>(msg => msg.Value.Length > 0)
        );
    }
}