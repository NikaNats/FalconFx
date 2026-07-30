using Projects;

namespace FalconFX.AppHost;

public class AppHostProgram
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // 1. Kafka + Admin UI (http://localhost:8080)
        var kafka = builder.AddKafka("kafka")
            .WithKafkaUI();

        // 2. Postgres + PgAdmin (http://localhost:5050)
        var postgres = builder.AddPostgres("postgres")
            .WithDataVolume()
            .WithPgAdmin();

        var tradeDb = postgres.AddDatabase("trade-db");

        // 3. Redis + Redis Commander (http://localhost:8081)
        var redis = builder.AddRedis("redis")
            .WithRedisCommander();

        // 4. Matching Engine (Consumes orders from Kafka, Produces trades to Kafka)
        var matchingEngine = builder.AddProject<FalconFX_MatchingEngine>("matching-engine")
            .WithReference(kafka)
            .WaitFor(kafka);

        // 5. Market Maker (Generates synthetic order flow)
        // 🔥 Wait for MatchingEngine to be ready before starting order stream
        var marketMaker = builder.AddProject<FalconFX_MarketMaker>("market-maker")
            .WithReference(kafka)
            .WaitFor(kafka)
            .WaitFor(matchingEngine);

        // 6. Trade Processor (Saves trades to DB & updates Redis tickers)
        var tradeProcessor = builder.AddProject<FalconFX_TradeProcessor>("trade-processor")
            .WithReference(kafka)
            .WithReference(tradeDb)
            .WithReference(redis)
            .WaitFor(kafka)
            .WaitFor(tradeDb)
            .WaitFor(redis);

        // 7. Gateway (SignalR Hub for Browser Clients)
        var gateway = builder.AddProject<FalconFX_Gateway>("gateway")
            .WithReference(redis)
            .WaitFor(redis)
            .WithExternalHttpEndpoints(); // Serves wwwroot/index.html & SignalR

        builder.Build().Run();
    }
}