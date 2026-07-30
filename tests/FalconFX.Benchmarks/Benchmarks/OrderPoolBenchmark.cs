using BenchmarkDotNet.Attributes;
using FalconFX.MatchingEngine;

namespace FalconFX.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class OrderPoolBenchmark
{
    private const int N = 100_000;
    private OrderPool _pool = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new OrderPool(N);
    }

    [Benchmark(Baseline = true)]
    public void FalconFX_StructPool_RentAndReturn()
    {
        _pool.Reset();
        for (var i = 0; i < N; i++)
        {
            var idx = _pool.Rent();
            ref var node = ref _pool.Get(idx);
            node.Id = i;
            node.Price = 100;
            _pool.Return(idx);
        }
    }

    [Benchmark]
    public List<HeapOrderNode> Traditional_Heap_Allocations()
    {
        var list = new List<HeapOrderNode>(N);
        for (var i = 0; i < N; i++) list.Add(new HeapOrderNode { Id = i, Price = 100 });
        return list;
    }
}

public class HeapOrderNode
{
    public long Id { get; set; }
    public long Price { get; set; }
}