using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aspire.Hosting.Testing;
using Confluent.Kafka;
using FalconFX.AppHost;
using FalconFX.Protos;
using Google.Protobuf;
using Npgsql;

namespace FalconFX.Benchmarks;

/// <summary>
/// Clean End-to-End System Benchmark.
/// Starts the full Aspire stack but disables the continuous MarketMaker
/// so we measure pure pipeline throughput of a controlled order burst.
/// </summary>
public static class SystemE2EBenchmark
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine("AUTOMATED TRUE END-TO-END SYSTEM BENCHMARK (Clean)");
        Console.WriteLine("=================================================");
        Console.WriteLine("Starting .NET Aspire Orchestration Host...");

        // Tell MarketMaker to stay idle
        Environment.SetEnvironmentVariable("BENCHMARK_MODE", "true");

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<AppHostProgram>()
            .ConfigureAwait(false);

        await using var app = await appHost.BuildAsync().ConfigureAwait(false);
        await app.StartAsync().ConfigureAwait(false);

        Console.WriteLine("✅ AppHost started successfully.");

        // Resolve endpoints
        var kafkaBootstrap = (await app.GetConnectionStringAsync("kafka").ConfigureAwait(false) ?? "127.0.0.1:9092")
            .Replace("localhost", "127.0.0.1");

        var postgresConnString = (await app.GetConnectionStringAsync("trade-db").ConfigureAwait(false) ??
                                  "Host=127.0.0.1;Port=5432;Database=trade-db;Username=postgres;Password=postgres")
            .Replace("localhost", "127.0.0.1");

        Console.WriteLine($"✅ Kafka Address: {kafkaBootstrap}");

        const int totalOrders = 50_000;
        const int expectedTrades = totalOrders / 2;   // alternating buy/sell @ same price

        Console.WriteLine("⏳ Waiting for PostgreSQL 'trade-db' and 'Trades' table to be created...");
        long initialCount = -1;
        for (var i = 0; i < 30; i++)
        {
            initialCount = await GetTradeCountAsync(postgresConnString, truncate: true).ConfigureAwait(false);
            if (initialCount >= 0) break;
            await Task.Delay(2000).ConfigureAwait(false);
        }

        if (initialCount < 0)
        {
            Console.WriteLine("❌ ERROR: Could not connect to PostgreSQL database or Trades table does not exist.");
            return;
        }

        Console.WriteLine($"✅ Clean starting trade count: {initialCount:N0}");

        // Producer Configuration
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = kafkaBootstrap,
            LingerMs = 5,
            BatchSize = 1_048_576,
            BatchNumMessages = 50_000,
            MessageMaxBytes = 900_000,          // უსაფრთხო ლიმიტი კლიენტისთვის
            Acks = Acks.Leader,
            EnableDeliveryReports = false,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = 10_000,
            SocketTimeoutMs = 10_000
        };

        using var producer = new ProducerBuilder<long, SubmitOrderRequest>(producerConfig)
            .SetKeySerializer(Serializers.Int64)
            .SetValueSerializer(ProtobufSubmitOrderSerializer.Instance)
            .Build();

        var request = new SubmitOrderRequest { Quantity = 10 };
        var message = new Message<long, SubmitOrderRequest>();

        // Kafka Consumer-ების მოთელვა
        Console.WriteLine("\n⏳ Waiting 5 seconds for Kafka Consumer Groups to stabilize (Cold Start Fix)...");
        await Task.Delay(5000).ConfigureAwait(false);

        Console.WriteLine($"\n🚀 Producing {totalOrders:N0} orders...");

        var sw = Stopwatch.StartNew();

        for (long i = 1; i <= totalOrders; i++)
        {
            request.Id = i;
            request.Side = (i % 2 == 0) ? 1 : 2;   // Buy / Sell alternating
            request.Price = 100;

            message.Key = i;
            message.Value = request;

            producer.Produce("orders", message);

            if (i % 5_000 == 0)
                producer.Poll(TimeSpan.Zero);
        }

        producer.Flush(TimeSpan.FromSeconds(15));
        var produceMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"✅ Flushed {totalOrders:N0} orders in {produceMs} ms");

        // Wait for persistence
        Console.WriteLine("⏳ Waiting for trades to land in PostgreSQL...");

        long currentCount = initialCount;
        var lastReport = sw.Elapsed;

        while (currentCount < initialCount + expectedTrades)
        {
            await Task.Delay(200).ConfigureAwait(false);
            currentCount = await GetTradeCountAsync(postgresConnString).ConfigureAwait(false);

            if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(2))
            {
                Console.WriteLine($"  ... {currentCount - initialCount:N0} / {expectedTrades:N0} trades persisted");
                lastReport = sw.Elapsed;
            }

            if (sw.Elapsed.TotalSeconds > 45)
            {
                Console.WriteLine($"❌ Timeout after 45s. Got {currentCount - initialCount:N0} trades.");
                break;
            }
        }

        sw.Stop();

        var persisted = currentCount - initialCount;
        var totalSec = sw.Elapsed.TotalSeconds;
        var throughput = totalOrders / totalSec;
        var latencyMs = (totalSec * 1000) / totalOrders;

        Console.WriteLine("\n=================================================");
        Console.WriteLine("E2E BENCHMARK RESULTS");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Orders sent:              {totalOrders:N0}");
        Console.WriteLine($"Trades persisted:         {persisted:N0}");
        Console.WriteLine($"Total time:               {totalSec:F2} s");
        Console.WriteLine($"Throughput:               {throughput:N0} orders/sec");
        Console.WriteLine($"Avg latency:              {latencyMs:F3} ms/order");
        Console.WriteLine("=================================================\n");
    }

    private static async Task<long> GetTradeCountAsync(string connectionString, bool truncate = false)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(connectionString)
            {
                GssEncryptionMode = GssEncryptionMode.Disable
            };

            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            if (truncate)
            {
                await using var trunc = new NpgsqlCommand(
                    "TRUNCATE TABLE \"Trades\" RESTART IDENTITY;", conn);
                await trunc.ExecuteNonQueryAsync().ConfigureAwait(false);
                Console.WriteLine("🧹 Truncated Trades table.");
            }

            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"Trades\"", conn);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result is null ? 0 : Convert.ToInt64(result);
        }
        catch
        {
            return -1;
        }
    }
}

public sealed class ProtobufSubmitOrderSerializer : ISerializer<SubmitOrderRequest>
{
    public static readonly ProtobufSubmitOrderSerializer Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Serialize(SubmitOrderRequest data, SerializationContext context)
    {
        var size = data.CalculateSize();
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        data.WriteTo(buffer);
        return buffer;
    }
}