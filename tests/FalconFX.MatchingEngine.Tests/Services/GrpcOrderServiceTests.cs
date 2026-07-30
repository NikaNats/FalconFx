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
        // Arrange
        var kafkaProducer = Substitute.For<IProducer<Null, byte[]>>();
        var engineWorker = new EngineWorker(NullLogger<EngineWorker>.Instance, kafkaProducer);
        var service = new GrpcOrderService(engineWorker, NullLogger<GrpcOrderService>.Instance);

        var requestStream = Substitute.For<IAsyncStreamReader<SubmitOrderRequest>>();
        var serverCallContext = Substitute.For<ServerCallContext>();

        var ordersToStream = new List<SubmitOrderRequest>
        {
            new() { Id = 1, Side = 1, Price = 100, Quantity = 10 },
            new() { Id = 2, Side = 2, Price = 100, Quantity = 10 }
        };

        var index = -1;
        requestStream.MoveNext(Arg.Any<CancellationToken>()).Returns(call =>
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

        await Task.Delay(100);
        await engineWorker.StopAsync(cts.Token);

        // Assert
        response.Success.Should().BeTrue();

        // Ensure trade was executed and sent to Kafka
        kafkaProducer.Received(1).Produce(Arg.Is("trades"), Arg.Any<Message<Null, byte[]>>());
    }
}