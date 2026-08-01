using System.Text;
using Projects;

namespace FalconFX.AppHost;

public class AppHostProgram
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var builder = DistributedApplication.CreateBuilder(args);

        // 1. Kafka + Kafka UI
        var kafka = builder.AddKafka("kafka")
            .WithEnvironment("KAFKA_HEAP_OPTS", "-Xms512m -Xmx1024m")
            .WithEnvironment("KAFKA_LOG4J_LOGGERS",
                "kafka.controller=WARN,state.change.logger=WARN,kafka.log.LogLoader=WARN,kafka.coordinator.group=WARN")
            .WithKafkaUI();

        // 2. Postgres + PgAdmin
        var postgres = builder.AddPostgres("postgres")
            .WithDataVolume()
            .WithPgAdmin();

        var tradeDb = postgres.AddDatabase("trade-db");

        // 3. Redis + Redis Commander
        var redis = builder.AddRedis("redis")
            .WithRedisCommander();

        // 4. Matching Engine
        var matchingEngine = builder.AddProject<FalconFX_MatchingEngine>("matching-engine")
            .WithReference(kafka)
            .WaitFor(kafka);

        // 5. Market Maker
        var marketMaker = builder.AddProject<FalconFX_MarketMaker>("market-maker")
            .WithReference(kafka)
            .WaitFor(kafka)
            .WaitFor(matchingEngine);

        // Only force idle mode when the clean E2E benchmark is running
        if (string.Equals(
                Environment.GetEnvironmentVariable("BENCHMARK_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            marketMaker = marketMaker.WithEnvironment("BENCHMARK_MODE", "true");

        // 6. Trade Processor
        var tradeProcessor = builder.AddProject<FalconFX_TradeProcessor>("trade-processor")
            .WithReference(kafka)
            .WithReference(tradeDb)
            .WithReference(redis)
            .WaitFor(kafka)
            .WaitFor(tradeDb)
            .WaitFor(redis);

        // 7. Gateway
        var gateway = builder.AddProject<FalconFX_Gateway>("gateway")
            .WithReference(redis)
            .WaitFor(redis)
            .WithExternalHttpEndpoints(); // Serves wwwroot/index.html & SignalR

        builder.Build().Run();
    }
}