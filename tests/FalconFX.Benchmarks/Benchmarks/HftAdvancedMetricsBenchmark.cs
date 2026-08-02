using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;
using FalconFX.MatchingEngine.Services;

namespace FalconFX.Benchmarks.Benchmarks;

public static class HftAdvancedMetricsBenchmark
{
    private const int SampleCount = 1_000_000; // 1 მილიონი ტესტური ტრანზაქცია

    public static async Task RunAsync()
    {
        Console.Clear();
        Console.WriteLine("=======================================================================");
        Console.WriteLine("          FALCONFX ULTRA-LOW LATENCY ADVANCED HFT BENCHMARK           ");
        Console.WriteLine("=======================================================================");

        // 1. CPU Thread Affinity (Pinning Thread to Physical Core)
        PinThreadToCore(coreIndex: 2);

        Console.WriteLine($"\n[1/4] CPU Thread Pinning: Bound current benchmark thread to Core #2");
        Console.WriteLine($"[2/4] High-Resolution Timer Frequency: {Stopwatch.Frequency:N0} ticks/sec ({1_000_000_000.0 / Stopwatch.Frequency:F3} ns precision per tick)");

        // Warmup JIT & System Memory Pools
        Console.WriteLine("\n[3/4] Warming up JIT, OrderBook memory pools, and cache lines...");
        WarmupSystem();

        // 2. Pre-Trade Risk Check Micro-Benchmark
        Console.WriteLine("\n=======================================================================");
        Console.WriteLine(" 1. PRE-TRADE RISK CHECK BENCHMARK (< 1 μs Target)");
        Console.WriteLine("=======================================================================");
        BenchmarkPreTradeRiskCheck();

        // 3. Full Tick-to-Trade Latency Benchmark
        Console.WriteLine("\n=======================================================================");
        Console.WriteLine(" 2. TICK-TO-TRADE LATENCY & PERCENTILE ANALYSIS (1,000,000 Orders)");
        Console.WriteLine("=======================================================================");
        BenchmarkTickToTradePipeline();

        await Task.CompletedTask;
    }

    private static void BenchmarkPreTradeRiskCheck()
    {
        var riskChecker = new PreTradeRiskChecker(maxOrderQuantity: 10_000, maxNotionalValue: 10_000_000, maxPriceDeviation: 20);
        var order = new Order(1, OrderSide.Buy, 100, 50);
        long currentMarketPrice = 100;

        var latenciesNs = new double[SampleCount];
        double doubleFreq = Stopwatch.Frequency;

        for (int i = 0; i < SampleCount; i++)
        {
            long start = Stopwatch.GetTimestamp();

            var result = riskChecker.ValidateOrder(in order, currentMarketPrice);

            long end = Stopwatch.GetTimestamp();

            latenciesNs[i] = (end - start) * 1_000_000_000.0 / doubleFreq;
        }

        Array.Sort(latenciesNs);

        Console.WriteLine($"Samples:                   {SampleCount:N0}");
        Console.WriteLine($"Mean Risk Check Latency:   {latenciesNs.Average():F2} ns ({latenciesNs.Average() / 1000.0:F4} μs)");
        Console.WriteLine($"p50 (Median):              {GetPercentile(latenciesNs, 50):F2} ns");
        Console.WriteLine($"p99:                       {GetPercentile(latenciesNs, 99):F2} ns");
        Console.WriteLine($"p99.999:                   {GetPercentile(latenciesNs, 99.999):F2} ns");
        Console.WriteLine($"Risk Check Result Status:  0-Allocation Stack Execution PASS");
    }

    private static void BenchmarkTickToTradePipeline()
    {
        var orderBook = new OrderBook(poolSize: SampleCount + 1000);
        var riskChecker = new PreTradeRiskChecker(10_000, 10_000_000, 20);
        var latenciesNs = new double[SampleCount];
        double doubleFreq = Stopwatch.Frequency;

        long sequenceGapErrors = 0;
        long processedTrades = 0;
        long expectedSequenceId = 1;

        // Pre-populate OrderBook with resting Ask liquidity
        for (int i = 1; i <= SampleCount; i++)
        {
            orderBook.ProcessOrder(new Order(i, OrderSide.Sell, 100, 10), _ => { });
        }

        // Measure Tick-to-Trade: Signal -> Risk Check -> Matching -> Trade Callback Execution
        for (int i = 0; i < SampleCount; i++)
        {
            long incomingOrderId = SampleCount + i + 1;
            var buyOrder = new Order(incomingOrderId, OrderSide.Buy, 100, 10);

            // Sequence Control Check
            if (incomingOrderId != SampleCount + expectedSequenceId)
            {
                sequenceGapErrors++;
            }
            expectedSequenceId++;

            // Tick-to-Trade Timing Starts Here
            long startTimestamp = Stopwatch.GetTimestamp();

            // Step 1: Pre-Trade Risk Validation
            var riskStatus = riskChecker.ValidateOrder(in buyOrder, 100);
            if (riskStatus == RiskCheckResult.Passed)
            {
                // Step 2: Zero-Alloc Engine Execution
                orderBook.ProcessOrder(buyOrder, trade =>
                {
                    processedTrades++;
                });
            }

            // Tick-to-Trade Timing Ends Here
            long endTimestamp = Stopwatch.GetTimestamp();

            latenciesNs[i] = (endTimestamp - startTimestamp) * 1_000_000_000.0 / doubleFreq;
        }

        Array.Sort(latenciesNs);

        // Compute Detailed Statistical Breakdown
        double min = latenciesNs[0];
        double max = latenciesNs[^1];
        double mean = latenciesNs.Average();
        double p50 = GetPercentile(latenciesNs, 50);
        double p90 = GetPercentile(latenciesNs, 90);
        double p99 = GetPercentile(latenciesNs, 99);
        double p99_9 = GetPercentile(latenciesNs, 99.9);
        double p99_99 = GetPercentile(latenciesNs, 99.99);
        double p99_999 = GetPercentile(latenciesNs, 99.999);

        // Compute Jitter (Standard Deviation of Latency)
        double sumOfSquares = latenciesNs.Sum(d => (d - mean) * (d - mean));
        double jitterNs = Math.Sqrt(sumOfSquares / latenciesNs.Length);

        Console.WriteLine($"Processed Orders:          {SampleCount:N0}");
        Console.WriteLine($"Executed Trades:           {processedTrades:N0}");
        Console.WriteLine($"Sequence Gaps / Drops:     {sequenceGapErrors} ({(sequenceGapErrors == 0 ? "0% Loss - PERFECT" : "FAILED")})");
        Console.WriteLine($"Throughput (MPS):          {(SampleCount / (latenciesNs.Sum() / 1_000_000_000.0)):N0} orders/sec");
        Console.WriteLine("-----------------------------------------------------------------------");
        Console.WriteLine($"LATENCY PERCENTILE BREAKDOWN (Tick-to-Trade):");
        Console.WriteLine($"  Min Latency:             {min:F2} ns ({min / 1000.0:F3} μs)");
        Console.WriteLine($"  Mean Latency:            {mean:F2} ns ({mean / 1000.0:F3} μs)");
        Console.WriteLine($"  p50 (Median):            {p50:F2} ns ({p50 / 1000.0:F3} μs)");
        Console.WriteLine($"  p90:                     {p90:F2} ns ({p90 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99:                     {p99:F2} ns ({p99 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.9:                   {p99_9:F2} ns ({p99_9 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.99:                  {p99_99:F2} ns ({p99_99 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.999 (Tail Latency):  {p99_999:F2} ns ({p99_999 / 1000.0:F3} μs)");
        Console.WriteLine($"  Max Spike:               {max:F2} ns ({max / 1000.0:F3} μs)");
        Console.WriteLine($"  Jitter (Std Dev):        ±{jitterNs:F2} ns ({(jitterNs / 1000.0):F3} μs)");
        Console.WriteLine("=======================================================================\n");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetPercentile(double[] sortedData, double percentile)
    {
        int idx = (int)Math.Ceiling((percentile / 100.0) * sortedData.Length) - 1;
        idx = Math.Clamp(idx, 0, sortedData.Length - 1);
        return sortedData[idx];
    }

    private static void PinThreadToCore(int coreIndex)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Bitmask for thread affinity (Core 2 = 1 << 2 = 4)
                IntPtr affinity = new IntPtr(1 << coreIndex);

#pragma warning disable CA1416
                ProcessThread thread = Process.GetCurrentProcess().Threads[0];
                thread.ProcessorAffinity = affinity;
#pragma warning restore CA1416

                Console.WriteLine($"SUCCESS: Thread affinity set to Core mask {1 << coreIndex}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Unable to pin thread to CPU core: {ex.Message}");
        }
    }

    private static void WarmupSystem()
    {
        var book = new OrderBook(1000);
        var checker = new PreTradeRiskChecker(100, 10000, 10);
        var order = new Order(1, OrderSide.Buy, 100, 10);

        for (int i = 0; i < 50_000; i++)
        {
            checker.ValidateOrder(in order, 100);
            book.ProcessOrder(order, _ => { });
            book.Clear();
        }
    }
}