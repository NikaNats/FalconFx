using System.Text;
using BenchmarkDotNet.Running;
using FalconFX.Benchmarks.Benchmarks;

namespace FalconFX.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "--hft")
        {
            await HftAdvancedMetricsBenchmark.RunAsync().ConfigureAwait(false);
            return;
        }

        if (args.Length > 0 && args[0] == "--e2e")
        {
            await SystemE2EBenchmark.RunAsync().ConfigureAwait(false);
            return;
        }

        if (args.Length > 0 && args[0] == "--real")
        {
            await RealPipelineBenchmark.RunAsync().ConfigureAwait(false);
            return;
        }

        Console.WriteLine("Select Benchmark Execution Level:");
        Console.WriteLine("1. Advanced HFT Latency & Tail Percentile Analysis (Tick-to-Trade, p99.999, Risk Check, CPU Pinning)");
        Console.WriteLine("2. Automated True End-to-End System Benchmark (Aspire + Kafka + Engine + PostgreSQL)");
        Console.WriteLine("3. Internal Async Pipeline Benchmark (Protobuf SerDe + Channels + Thread)");
        Console.WriteLine("4. BenchmarkDotNet Micro-benchmarks (Pure OrderBook & Memory Pool BDN)");
        Console.Write("\nEnter selection (1, 2, 3, or 4): ");

        var input = Console.ReadLine()?.Trim();

        if (input == "1")
            await HftAdvancedMetricsBenchmark.RunAsync().ConfigureAwait(false);
        else if (input == "2")
            await SystemE2EBenchmark.RunAsync().ConfigureAwait(false);
        else if (input == "3")
            await RealPipelineBenchmark.RunAsync().ConfigureAwait(false);
        else
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}