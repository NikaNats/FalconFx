using Confluent.Kafka;
using FalconFX.MarketMaker;
using FalconFX.Protos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Kafka;
using Xunit;

namespace FalconFX.Tests.Integration;

public class WorkerIntegrationTests : IAsyncLifetime
{
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
    public async Task Worker_ShouldProduceValidProtobufOrdersToKafka()
    {
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

        var workerTask = worker.StartAsync(cts.Token);

        await Task.Delay(7000);

        await cts.CancelAsync();
        await workerTask;

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "test-verifier-group",
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<Null, byte[]>(consumerConfig).Build();
        consumer.Subscribe("orders");

        var consumeResult = consumer.Consume(TimeSpan.FromSeconds(10));

        consumeResult.Should().NotBeNull("Worker-ს უნდა გაეგზავნა შეტყობინებები Kafka-ში");
        consumeResult.Message.Value.Should().NotBeNull();

        var orderRequest = SubmitOrderRequest.Parser.ParseFrom(consumeResult.Message.Value);

        orderRequest.Id.Should().BeGreaterThan(0);
        orderRequest.Price.Should().BeInRange(99, 101);
        orderRequest.Side.Should().BeOneOf(1, 2);
        orderRequest.Quantity.Should().Be(10);
    }
}