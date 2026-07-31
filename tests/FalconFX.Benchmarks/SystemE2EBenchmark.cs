using System.Diagnostics;
using Aspire.Hosting.Testing;
using Confluent.Kafka;
using FalconFX.AppHost;
using FalconFX.Protos;
using Google.Protobuf;
using Npgsql;

namespace FalconFX.Benchmarks;

/// <summary>
///     Automated End-to-End System Benchmark.
///     Orchestrates the .NET Aspire environment in-process, retrieves connection strings dynamically,
///     produces order flow into Kafka, and measures PostgreSQL trade persistence latency.
/// </summary>
public class SystemE2EBenchmark
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine("AUTOMATED TRUE END-TO-END SYSTEM BENCHMARK");
        Console.WriteLine("=================================================");
        Console.WriteLine("Starting .NET Aspire Orchestration Host...");

        // 1. Automatically start .NET Aspire AppHost
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHostProgram>().ConfigureAwait(false);
        await using var app = await appHost.BuildAsync().ConfigureAwait(false);
        await app.StartAsync().ConfigureAwait(false);

        Console.WriteLine("AppHost Orchestration Host started successfully.");

        // 2. Resolve Kafka and PostgreSQL connection strings dynamically
        var kafkaBootstrap = await app.GetConnectionStringAsync("kafka").ConfigureAwait(false) ?? "127.0.0.1:9092";
        var postgresConnString = await app.GetConnectionStringAsync("trade-db").ConfigureAwait(false) ??
                                 "Host=127.0.0.1;Port=5432;Database=trade-db;Username=postgres;Password=postgres";

        // Normalize loopback hostname for Windows IPv6 resolution compatibility
        kafkaBootstrap = kafkaBootstrap.Replace("localhost", "127.0.0.1");
        postgresConnString = postgresConnString.Replace("localhost", "127.0.0.1");

        Console.WriteLine($"Discovered Kafka Endpoint: {kafkaBootstrap}");
        Console.WriteLine("Discovered PostgreSQL Connection String.");

        const int totalOrders = 50_000;

        // Wait for PostgreSQL container readiness (up to 15 seconds)
        long initialTradeCount = -1;
        for (var i = 0; i < 15; i++)
        {
            initialTradeCount = await GetDatabaseTradeCountAsync(postgresConnString).ConfigureAwait(false);
            if (initialTradeCount >= 0) break;
            await Task.Delay(1000).ConfigureAwait(false);
        }

        if (initialTradeCount < 0)
        {
            Console.WriteLine("ERROR: Failed to connect to PostgreSQL container.");
            return;
        }

        Console.WriteLine($"Initial PostgreSQL Trades Count: {initialTradeCount:N0}");

        // 3. Kafka Producer Setup
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = kafkaBootstrap,
            LingerMs = 5,
            BatchSize = 512 * 1024,
            Acks = Acks.Leader,
            EnableDeliveryReports = false,
            SocketTimeoutMs = 5000,
            MessageTimeoutMs = 5000
        };

        using var producer = new ProducerBuilder<Null, byte[]>(producerConfig).Build();
        var protoReq = new SubmitOrderRequest { Quantity = 10 };

        Console.WriteLine($"\nProducing {totalOrders:N0} orders to Kafka 'orders' topic...");

        var sw = Stopwatch.StartNew();

        try
        {
            for (long i = 1; i <= totalOrders; i++)
            {
                protoReq.Id = i;
                protoReq.Side = i % 2 == 0 ? 1 : 2;
                protoReq.Price = 100;

                var bytes = protoReq.ToByteArray();
                producer.Produce("orders", new Message<Null, byte[]> { Value = bytes });

                if (i % 10_000 == 0) producer.Poll(TimeSpan.Zero);
            }

            producer.Flush(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Kafka Produce Error: {ex.Message}");
            return;
        }

        var produceTime = sw.ElapsedMilliseconds;
        Console.WriteLine(
            $"{totalOrders:N0} orders flushed to Kafka in {produceTime} ms. Awaiting PostgreSQL persistence...");

        // 4. Await TradeProcessor PostgreSQL Bulk Persistence
        var expectedTradeCount = initialTradeCount + totalOrders / 2;
        var currentCount = initialTradeCount;

        while (currentCount < expectedTradeCount)
        {
            await Task.Delay(100).ConfigureAwait(false);
            currentCount = await GetDatabaseTradeCountAsync(postgresConnString).ConfigureAwait(false);

            if (sw.Elapsed.TotalSeconds > 30)
            {
                Console.WriteLine(
                    $"Timeout (30s): Current DB Trades: {currentCount:N0} / Expected: {expectedTradeCount:N0}");
                break;
            }
        }

        sw.Stop();

        // 5. Calculate System E2E Metrics
        var totalSeconds = sw.Elapsed.TotalSeconds;
        var processedTrades = currentCount - initialTradeCount;
        var e2eThroughput = totalOrders / totalSeconds;
        var avgE2ELatencyMs = totalSeconds * 1000 / totalOrders;

        Console.WriteLine("\nAUTOMATED TRUE END-TO-END SYSTEM BENCHMARK RESULTS:");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Total E2E Time (Net + Engine + DB): {totalSeconds:F2} seconds");
        Console.WriteLine($"PostgreSQL Persisted Trades: {processedTrades:N0}");
        Console.WriteLine($"True System E2E Throughput: {e2eThroughput:N0} orders/sec");
        Console.WriteLine($"True System E2E Avg Latency: {avgE2ELatencyMs:F3} ms/order");
        Console.WriteLine("=================================================\n");

        Console.WriteLine("Cleaning up Aspire environment...");
    }

    private static async Task<long> GetDatabaseTradeCountAsync(string connectionString)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"Trades\"", conn);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result != null ? Convert.ToInt64(result) : 0;
        }
        catch
        {
            return -1;
        }
    }
}