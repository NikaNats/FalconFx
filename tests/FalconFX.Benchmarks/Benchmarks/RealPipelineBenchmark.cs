using System.Diagnostics;
using Confluent.Kafka;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FalconFX.Benchmarks.Benchmarks;

/// <summary>
///     In-memory async pipeline benchmark measuring end-to-end execution speed:
///     Protobuf Deserialization -> System.Threading.Channels -> EngineWorker Async Thread -> Trade Serialization.
/// </summary>
public class RealPipelineBenchmark
{
    public static async Task RunAsync(int totalOrders = 500_000)
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine($"Starting Internal Async Pipeline Benchmark ({totalOrders:N0} orders)...");
        Console.WriteLine("=================================================");

        // Arrange mock Kafka producer and core Matching Engine worker
        var kafkaProducer = Substitute.For<IProducer<long, TradeExecuted>>();
        var engineWorker = new EngineWorker(NullLogger<EngineWorker>.Instance, kafkaProducer);

        using var cts = new CancellationTokenSource();
        await engineWorker.StartAsync(cts.Token).ConfigureAwait(false);

        // Prepare Protobuf payload bytes
        var protoOrderBuy = new SubmitOrderRequest { Id = 1, Side = 1, Price = 100, Quantity = 10 };
        var protoOrderSell = new SubmitOrderRequest { Id = 2, Side = 2, Price = 100, Quantity = 10 };

        var buyBytes = protoOrderBuy.ToByteArray();
        var sellBytes = protoOrderSell.ToByteArray();

        var sw = Stopwatch.StartNew();

        // Act: Protobuf Parse -> System.Threading.Channels -> Async Engine Worker Loop
        for (var i = 0; i < totalOrders / 2; i++)
        {
            var buyReq = SubmitOrderRequest.Parser.ParseFrom(buyBytes);
            engineWorker.EnqueueOrder(new Order(buyReq.Id, (OrderSide)buyReq.Side, buyReq.Price, buyReq.Quantity));

            var sellReq = SubmitOrderRequest.Parser.ParseFrom(sellBytes);
            engineWorker.EnqueueOrder(new Order(sellReq.Id, (OrderSide)sellReq.Side, sellReq.Price, sellReq.Quantity));
        }

        // Allow channel buffer to drain completely
        await Task.Delay(500, cts.Token).ConfigureAwait(false);
        sw.Stop();

        await engineWorker.StopAsync(cts.Token).ConfigureAwait(false);

        // Subtract channel drain delay (500ms) for accurate active execution time
        var elapsedSeconds = sw.Elapsed.TotalSeconds - 0.5;
        if (elapsedSeconds <= 0) elapsedSeconds = 0.001;

        var throughput = totalOrders / elapsedSeconds;
        var avgLatencyMicros = elapsedSeconds * 1_000_000 / totalOrders;

        Console.WriteLine("\nInternal Async Pipeline Benchmark Results:");
        Console.WriteLine($"Total Elapsed Execution Time: {elapsedSeconds:F3} seconds");
        Console.WriteLine($"Pipeline Throughput: {throughput:N0} orders/sec");
        Console.WriteLine($"Average Pipeline Latency: {avgLatencyMicros:F2} microseconds/order");
        Console.WriteLine("=================================================\n");
    }
}