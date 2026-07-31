using System.Text;
using BenchmarkDotNet.Running;
using FalconFX.Benchmarks.Benchmarks;

namespace FalconFX.Benchmarks;

/// <summary>
///     Benchmark suite execution entry point for FalconFX performance analysis.
///     Supports E2E Aspire benchmarking, internal async pipeline benchmarking, and BenchmarkDotNet micro-benchmarks.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // Configure UTF-8 encoding for cross-platform terminal compatibility
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // Command-line flag shortcuts for CI/CD or automated execution
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
        Console.WriteLine("1. Automated True End-to-End System Benchmark (Aspire + Kafka + Engine + PostgreSQL)");
        Console.WriteLine("2. Internal Async Pipeline Benchmark (Protobuf SerDe + Channels + Thread)");
        Console.WriteLine("3. BenchmarkDotNet Micro-benchmarks (Pure OrderBook & Memory Pool BDN)");
        Console.Write("\nEnter selection (1, 2, or 3): ");

        var input = Console.ReadLine()?.Trim();

        if (input == "1")
            await SystemE2EBenchmark.RunAsync().ConfigureAwait(false);
        else if (input == "2")
            await RealPipelineBenchmark.RunAsync().ConfigureAwait(false);
        else
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}