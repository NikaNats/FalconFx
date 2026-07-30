using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;

namespace FalconFX.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class SpanParsingBenchmark
{
    private const string Payload = "EURUSD:10550";
    private static readonly ConcurrentDictionary<string, string> SymbolPool = new();

    [Benchmark(Baseline = true)]
    public (string Symbol, long Price) FalconFX_ZeroAlloc_SpanParsing()
    {
        var span = Payload.AsSpan();
        var colonIdx = span.IndexOf(':');

        var symbolSpan = span[..colonIdx];
        var priceSpan = span[(colonIdx + 1)..];

        long.TryParse(priceSpan, out var price);

        var symbolKey = symbolSpan.ToString();
        var symbol = SymbolPool.GetOrAdd(symbolKey, symbolKey);

        return (symbol, price);
    }

    [Benchmark]
    public (string Symbol, long Price) Standard_StringSplit()
    {
        var parts = Payload.Split(':');
        var symbol = parts[0];
        long.TryParse(parts[1], out var price);

        return (symbol, price);
    }
}