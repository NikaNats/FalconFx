using System.Reflection;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.TradeProcessor;
using FalconFX.TradeProcessor.Data;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace FalconFX.IntegrationTests.Infrastructure;

public class InfrastructureChaosTests : IAsyncLifetime
{
    private readonly KafkaContainer _kafkaContainer = new KafkaBuilder("confluentinc/cp-kafka:7.6.12")
        .Build();

    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("trade-db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _kafkaContainer.StartAsync(cancellationToken);
        await _postgresContainer.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _kafkaContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task KafkaBroker_TransientDisconnect_ShouldRecoverAndProcessAllMessagesWithoutLoss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bootstrapServers = _kafkaContainer.GetBootstrapAddress();

        var producerConfig = new ProducerConfig { BootstrapServers = bootstrapServers };
        using var producer = new ProducerBuilder<long, SubmitOrderRequest>(producerConfig)
            .SetKeySerializer(Serializers.Int64)
            .SetValueSerializer(TestProtobufSubmitOrderSerializer.Instance)
            .Build();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "chaos-verification-group",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<Ignore, byte[]>(consumerConfig).Build();
        consumer.Subscribe("orders");

        for (long i = 1; i <= 5; i++)
            await producer.ProduceAsync("orders", new Message<long, SubmitOrderRequest>
            {
                Key = i,
                Value = new SubmitOrderRequest { Id = i, Price = 100, Quantity = 10, Side = 1 }
            }, cancellationToken);

        await _kafkaContainer.PauseAsync(cancellationToken);

        var backgroundProduceTask = Task.Run(async () =>
        {
            try
            {
                await producer.ProduceAsync("orders", new Message<long, SubmitOrderRequest>
                {
                    Key = 6,
                    Value = new SubmitOrderRequest { Id = 6, Price = 100, Quantity = 10, Side = 1 }
                }, cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);

        await Task.Delay(2000, cancellationToken);

        await _kafkaContainer.UnpauseAsync(cancellationToken);

        var produceSuccess = await backgroundProduceTask;

        var consumedCount = 0;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        while (!timeoutCts.Token.IsCancellationRequested && consumedCount < 6)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result != null) consumedCount++;
        }

        consumedCount.Should().Be(6);
    }

    [Fact]
    public async Task Postgres_ConnectionDropDuringBulkCopy_ShouldRetryAndPersistWhenDbComesBackOnline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var rawConnString = _postgresContainer.GetConnectionString();
        var kafkaBootstrap = _kafkaContainer.GetBootstrapAddress();

        var npgsqlBuilder = new NpgsqlConnectionStringBuilder(rawConnString)
        {
            Timeout = 2,
            CommandTimeout = 2
        };
        var resilientConnString = npgsqlBuilder.ConnectionString;

        var services = new ServiceCollection();
        services.AddDbContext<TradeDbContext>(options => options.UseNpgsql(resilientConnString));

        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                                                     CREATE TABLE IF NOT EXISTS "Trades" (
                                                         "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                                                         "MakerOrderId" bigint NOT NULL,
                                                         "TakerOrderId" bigint NOT NULL,
                                                         "Price" bigint NOT NULL,
                                                         "Quantity" bigint NOT NULL,
                                                         "Symbol" text NOT NULL,
                                                         "Timestamp" bigint NOT NULL,
                                                         "InsertedAt" timestamp with time zone NOT NULL
                                                     );
                                                 """, cancellationToken);
        }

        var mockConsumer = Substitute.For<IConsumer<Ignore, byte[]>>();
        var mockRedis = Substitute.For<IConnectionMultiplexer>();
        var mockRedisDb = Substitute.For<IDatabase>();
        mockRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(mockRedisDb);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:kafka"] = kafkaBootstrap,
                ["ConnectionStrings:trade-db"] = resilientConnString
            })
            .Build();

        var worker = new Worker(
            NullLogger<Worker>.Instance,
            serviceProvider,
            mockConsumer,
            mockRedis,
            config
        );

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerTask = worker.StartAsync(cts.Token);

        await Task.Delay(3000, cancellationToken);

        var record = new TradeRecord
        {
            MakerOrderId = 1,
            TakerOrderId = 2,
            Price = 10050,
            Quantity = 10,
            Symbol = "EURUSD",
            Timestamp = DateTime.UtcNow.Ticks
        };

        var consumeResult = new ConsumeResult<Ignore, byte[]>
        {
            Topic = "trades",
            Partition = 0,
            Offset = 1,
            Message = new Message<Ignore, byte[]> { Value = Array.Empty<byte>() }
        };

        var channelField = typeof(Worker).GetField("_tradeChannel",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var channel = (Channel<TradeWorkItem>)channelField!.GetValue(worker)!;

        await _postgresContainer.PauseAsync(cancellationToken);

        await channel.Writer.WriteAsync(new TradeWorkItem(record, consumeResult), cancellationToken);

        await Task.Delay(4000, cancellationToken);

        await _postgresContainer.UnpauseAsync(cancellationToken);

        await Task.Delay(4000, cancellationToken);

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
            var count = await db.Trades.CountAsync(cancellationToken);

            count.Should().Be(1,
                "the trade should be saved in the database exactly once after the connection is restored");
        }

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class TestProtobufSubmitOrderSerializer : ISerializer<SubmitOrderRequest>
    {
        public static readonly TestProtobufSubmitOrderSerializer Instance = new();

        public byte[] Serialize(SubmitOrderRequest data, SerializationContext context)
        {
            var size = data.CalculateSize();
            var buffer = GC.AllocateUninitializedArray<byte>(size);
            data.WriteTo(buffer);
            return buffer;
        }
    }
}