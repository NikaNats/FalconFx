using BenchmarkDotNet.Attributes;
using FalconFX.MarketMaker;

namespace FalconFX.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RandomGeneratorBenchmark
{
    private XorShift64 _xorShift = new(12345);

    [Benchmark(Baseline = true)]
    public int FalconFX_XorShift64()
    {
        return _xorShift.Next(99, 102);
    }

    [Benchmark]
    public int Standard_RandomShared()
    {
        return Random.Shared.Next(99, 102);
    }
}