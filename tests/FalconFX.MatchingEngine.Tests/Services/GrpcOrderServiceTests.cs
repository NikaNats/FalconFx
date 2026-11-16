using Confluent.Kafka;
using FalconFX.MatchingEngine.Services;
using FalconFX.Protos;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FalconFX.MatchingEngine.Tests.Services;

public class GrpcOrderServiceTests
{
    [Fact]
    public async Task StreamOrders_ShouldEnqueueAllIncomingOrders()
    {
        // Arrange: Mock the typed Kafka producer required by EngineWorker
        var kafkaProducer = Substitute.For<IProducer<long, TradeExecuted>>();
        var engineWorker = new EngineWorker(NullLogger<EngineWorker>.Instance, kafkaProducer);
        var service = new GrpcOrderService(engineWorker, NullLogger<GrpcOrderService>.Instance);

        var tradeProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kafkaProducer
            .When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<long, TradeExecuted>>(),
                Arg.Any<Action<DeliveryReport<long, TradeExecuted>>>()))
            .Do(_ => tradeProduced.TrySetResult());

        var requestStream = Substitute.For<IAsyncStreamReader<SubmitOrderRequest>>();
        var serverCallContext = Substitute.For<ServerCallContext>();

        var ordersToStream = new List<SubmitOrderRequest>
        {
            new() { Id = 1, Side = 1, Price = 100, Quantity = 10 },
            new() { Id = 2, Side = 2, Price = 100, Quantity = 10 }
        };

        var index = -1;
        requestStream.MoveNext(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            index++;
            if (index < ordersToStream.Count)
            {
                requestStream.Current.Returns(ordersToStream[index]);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });

        using var cts = new CancellationTokenSource();
        await engineWorker.StartAsync(cts.Token);

        // Act
        var response = await service.StreamOrders(requestStream, serverCallContext);

        // Wait for the deterministic signal that the matching engine produced the trade
        await tradeProduced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await engineWorker.StopAsync(cts.Token);

        // Assert
        response.Success.Should().BeTrue();

        // Ensure trade was executed and produced to Kafka as a typed TradeExecuted message
        kafkaProducer.Received(1).Produce(
            Arg.Is("trades"),
            Arg.Any<Message<long, TradeExecuted>>(),
            Arg.Any<Action<DeliveryReport<long, TradeExecuted>>>()
        );
    }
}
