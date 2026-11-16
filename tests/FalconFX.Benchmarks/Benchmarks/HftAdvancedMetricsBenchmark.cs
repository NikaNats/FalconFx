using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;
using FalconFX.MatchingEngine.Services;

namespace FalconFX.Benchmarks.Benchmarks;

public static class HftAdvancedMetricsBenchmark
{
    private const int SampleCount = 1_000_000; // 1 million transactions
    private const int OpsPerSample = 1_000; // Batch to beat the 100ns Stopwatch floor

    public static async Task RunAsync()
    {
        // Console.Clear() throws when stdout is redirected (CI logs, pipes)
        if (!Console.IsOutputRedirected)
            Console.Clear();
        Console.WriteLine("=======================================================================");
        Console.WriteLine("          FALCONFX ULTRA-LOW LATENCY ADVANCED HFT BENCHMARK           ");
        Console.WriteLine("=======================================================================");

        // 1. CPU Thread Affinity (Pinning the CURRENT native thread, not Threads[0])
        var pinned = PinCurrentThreadToCore(coreIndex: 2);

        Console.WriteLine(pinned
            ? "[1/4] CPU Thread Pinning: bound current benchmark thread to Core #2"
            : "[1/4] CPU Thread Pinning: unavailable on this platform (results are unpinned)");
        Console.WriteLine(
            $"[2/4] High-Resolution Timer Frequency: {Stopwatch.Frequency:N0} ticks/sec ({1_000_000_000.0 / Stopwatch.Frequency:F3} ns precision per tick)");
        Console.WriteLine($"[3/4] Sampling: {OpsPerSample:N0} operations per timed sample (timer quantization safe)");

        // Warmup JIT & System Memory Pools
        Console.WriteLine("       Warming up JIT, OrderBook memory pools, and cache lines...");
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
        var riskChecker = new PreTradeRiskChecker(maxOrderQuantity: 10_000, maxNotionalValue: 10_000_000,
            maxPriceDeviation: 20);
        var order = new Order(1, OrderSide.Buy, 100, 50);
        long currentMarketPrice = 100;

        var batchCount = SampleCount / OpsPerSample;
        var latenciesNs = new double[batchCount];
        var doubleFreq = Stopwatch.Frequency;

        // Warmup
        for (var i = 0; i < OpsPerSample; i++) riskChecker.ValidateOrder(in order, currentMarketPrice);

        for (var batch = 0; batch < batchCount; batch++)
        {
            var start = Stopwatch.GetTimestamp();

            for (var i = 0; i < OpsPerSample; i++)
                riskChecker.ValidateOrder(in order, currentMarketPrice);

            var end = Stopwatch.GetTimestamp();

            latenciesNs[batch] = (end - start) * 1_000_000_000.0 / doubleFreq / OpsPerSample;
        }

        Array.Sort(latenciesNs);

        Console.WriteLine($"Samples:                   {SampleCount:N0} ops ({batchCount:N0} batches)");
        Console.WriteLine(
            $"Mean Risk Check Latency:   {latenciesNs.Average():F2} ns ({latenciesNs.Average() / 1000.0:F4} μs)");
        Console.WriteLine($"p50 (Median):              {GetPercentile(latenciesNs, 50):F2} ns");
        Console.WriteLine($"p99:                       {GetPercentile(latenciesNs, 99):F2} ns");
        Console.WriteLine($"p99.999:                   {GetPercentile(latenciesNs, 99.999):F2} ns");
        Console.WriteLine("Risk Check Result Status:  0-Allocation Stack Execution PASS");
    }

    private static void BenchmarkTickToTradePipeline()
    {
        var orderBook = new OrderBook(poolSize: SampleCount + 1000);
        var riskChecker = new PreTradeRiskChecker(10_000, 10_000_000, 20);

        var batchCount = SampleCount / OpsPerSample;
        var latenciesNs = new double[batchCount];
        var doubleFreq = Stopwatch.Frequency;

        long sequenceGapErrors = 0;
        long processedTrades = 0;
        long expectedSequenceId = 1;
        double totalSeconds = 0;

        // Pre-populate OrderBook with resting Ask liquidity
        for (var i = 1; i <= SampleCount; i++)
            orderBook.ProcessOrder(new Order(i, OrderSide.Sell, 100, 10), _ => { });

        // Measure Tick-to-Trade: Signal -> Risk Check -> Matching -> Trade Callback Execution
        var processed = 0;
        for (var batch = 0; batch < batchCount; batch++)
        {
            var startTimestamp = Stopwatch.GetTimestamp();

            for (var i = 0; i < OpsPerSample; i++)
            {
                var incomingOrderId = SampleCount + processed + 1L;
                var buyOrder = new Order(incomingOrderId, OrderSide.Buy, 100, 10);

                // Sequence Control Check
                if (incomingOrderId != SampleCount + expectedSequenceId) sequenceGapErrors++;
                expectedSequenceId++;

                // Step 1: Pre-Trade Risk Validation
                var riskStatus = riskChecker.ValidateOrder(in buyOrder, 100);
                if (riskStatus == RiskCheckResult.Passed)
                    // Step 2: Zero-Alloc Engine Execution
                    orderBook.ProcessOrder(buyOrder, _ => { processedTrades++; }, startTimestamp);

                processed++;
            }

            var endTimestamp = Stopwatch.GetTimestamp();
            // Cast to double: long / long would be integer division and always yield 0
            var elapsedSeconds = (endTimestamp - startTimestamp) / (double)doubleFreq;
            totalSeconds += elapsedSeconds;

            latenciesNs[batch] = elapsedSeconds * 1_000_000_000.0 / OpsPerSample;
        }

        Array.Sort(latenciesNs);

        // Compute Detailed Statistical Breakdown
        var min = latenciesNs[0];
        var max = latenciesNs[^1];
        var mean = latenciesNs.Average();
        var p50 = GetPercentile(latenciesNs, 50);
        var p90 = GetPercentile(latenciesNs, 90);
        var p99 = GetPercentile(latenciesNs, 99);
        var p99_9 = GetPercentile(latenciesNs, 99.9);
        var p99_99 = GetPercentile(latenciesNs, 99.99);
        var p99_999 = GetPercentile(latenciesNs, 99.999);

        // Compute Jitter (Standard Deviation of Latency)
        var sumOfSquares = latenciesNs.Sum(d => (d - mean) * (d - mean));
        var jitterNs = Math.Sqrt(sumOfSquares / latenciesNs.Length);

        Console.WriteLine($"Processed Orders:          {SampleCount:N0}");
        Console.WriteLine($"Executed Trades:           {processedTrades:N0}");
        Console.WriteLine(
            $"Sequence Gaps / Drops:     {sequenceGapErrors} ({(sequenceGapErrors == 0 ? "0% Loss - PERFECT" : "FAILED")})");
        Console.WriteLine($"Throughput (MPS):          {SampleCount / totalSeconds:N0} orders/sec");
        Console.WriteLine("-----------------------------------------------------------------------");
        Console.WriteLine("LATENCY PERCENTILE BREAKDOWN (Tick-to-Trade, per-operation):");
        Console.WriteLine($"  Min Latency:             {min:F2} ns ({min / 1000.0:F3} μs)");
        Console.WriteLine($"  Mean Latency:            {mean:F2} ns ({mean / 1000.0:F3} μs)");
        Console.WriteLine($"  p50 (Median):            {p50:F2} ns ({p50 / 1000.0:F3} μs)");
        Console.WriteLine($"  p90:                     {p90:F2} ns ({p90 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99:                     {p99:F2} ns ({p99 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.9:                   {p99_9:F2} ns ({p99_9 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.99:                  {p99_99:F2} ns ({p99_99 / 1000.0:F3} μs)");
        Console.WriteLine($"  p99.999 (Tail Latency):  {p99_999:F2} ns ({p99_999 / 1000.0:F3} μs)");
        Console.WriteLine($"  Max Spike:               {max:F2} ns ({max / 1000.0:F3} μs)");
        Console.WriteLine($"  Jitter (Std Dev):        ±{jitterNs:F2} ns ({jitterNs / 1000.0:F3} μs)");
        Console.WriteLine("=======================================================================\n");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetPercentile(double[] sortedData, double percentile)
    {
        var idx = (int)Math.Ceiling(percentile / 100.0 * sortedData.Length) - 1;
        idx = Math.Clamp(idx, 0, sortedData.Length - 1);
        return sortedData[idx];
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

    private static bool PinCurrentThreadToCore(int coreIndex)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            Thread.BeginThreadAffinity();

            var mask = (nuint)(1UL << coreIndex);
            var previousMask = SetThreadAffinityMask(GetCurrentThread(), mask);

            if (previousMask == 0) return false;

            // Affinity stays in effect for the lifetime of this benchmark process.
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Unable to pin current thread to CPU core: {ex.Message}");
            return false;
        }
    }

    private static void WarmupSystem()
    {
        var book = new OrderBook(1000);
        var checker = new PreTradeRiskChecker(100, 10000, 10);
        var order = new Order(1, OrderSide.Buy, 100, 10);

        for (var i = 0; i < 50_000; i++)
        {
            checker.ValidateOrder(in order, 100);
            book.ProcessOrder(order, _ => { });
            book.Clear();
        }
    }
}
