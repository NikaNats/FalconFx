using Confluent.Kafka;
using FalconFX.MarketMaker;
using FalconFX.Protos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Kafka;

namespace FalconFX.IntegrationTests.Infrastructure;

public class KafkaIntegrationTests : IAsyncLifetime
{
    // Real Kafka Docker container initialized via Testcontainers
    private readonly KafkaContainer _kafkaContainer = new KafkaBuilder("confluentinc/cp-kafka:7.6.0")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _kafkaContainer.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _kafkaContainer.DisposeAsync();
    }

    [Fact]
    public async Task MarketMakerWorker_ShouldProduceValidProtobufOrders_ToKafkaTopic()
    {
        // Arrange
        var bootstrapServers = _kafkaContainer.GetBootstrapAddress();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:kafka"] = bootstrapServers
            })
            .Build();

        var logger = NullLogger<Worker>.Instance;
        var worker = new Worker(logger, config);

        using var cts = new CancellationTokenSource();

        // Act 1: Run MarketMaker Worker for 5 seconds
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(5000);
        await cts.CancelAsync();

        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        // Act 2: Consume messages from Kafka topic 'orders' (Using long key deserializer)
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "test-verification-group",
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<long, byte[]>(consumerConfig)
            .SetKeyDeserializer(Deserializers.Int64)
            .Build();

        consumer.Subscribe("orders");

        var consumeResult = consumer.Consume(TimeSpan.FromSeconds(10));

        // Assert: Verify Kafka contains valid Protobuf SubmitOrderRequest with OrderId Key
        consumeResult.Should().NotBeNull("MarketMaker should produce order messages to Kafka");
        consumeResult.Message.Key.Should().BeGreaterThan(0, "Kafka message key should contain valid OrderId");
        consumeResult.Message.Value.Should().NotBeNull();

        var orderRequest = SubmitOrderRequest.Parser.ParseFrom(consumeResult.Message.Value);

        orderRequest.Id.Should().BeGreaterThan(0);
        orderRequest.Price.Should().BeInRange(99, 101);
        orderRequest.Side.Should().BeOneOf(1, 2); // 1 = Buy, 2 = Sell
        orderRequest.Quantity.Should().Be(10);
    }
}