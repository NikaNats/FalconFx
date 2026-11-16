using System.Collections.Concurrent;
using System.Buffers.Text;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace FalconFX.Benchmarks.Benchmarks;

/// <summary>
///     Benchmarks the exact Gateway hot path: UTF-8 span parsing with a strict
///     full-slice price check plus a span alternate-lookup symbol pool.
/// </summary>
[MemoryDiagnoser]
public class SpanParsingBenchmark
{
    private const string PayloadText = "EURUSD:10550";
    private const int MaxSymbolChars = 64;

    private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(PayloadText);
    private static readonly ConcurrentDictionary<string, string> SymbolPool = new();

    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> SymbolLookup =
        SymbolPool.GetAlternateLookup<ReadOnlySpan<char>>();

    [Benchmark(Baseline = true)]
    public (string Symbol, long Price) FalconFX_ZeroAlloc_SpanParsing()
    {
        ReadOnlySpan<byte> span = PayloadBytes;
        var colonIdx = span.IndexOf((byte)':');

        var symbolBytes = span[..colonIdx];
        var priceBytes = span[(colonIdx + 1)..];

        if (!Utf8Parser.TryParse(priceBytes, out long price, out var bytesConsumed) ||
            bytesConsumed != priceBytes.Length)
            return (string.Empty, 0);

        Span<char> charBuffer = stackalloc char[MaxSymbolChars];
        var charCount = Encoding.UTF8.GetChars(symbolBytes, charBuffer);
        ReadOnlySpan<char> symbolChars = charBuffer[..charCount];

        if (!SymbolLookup.TryGetValue(symbolChars, out var symbol))
        {
            symbol = symbolChars.ToString();
            SymbolPool.TryAdd(symbol, symbol);
        }

        return (symbol, price);
    }

    [Benchmark]
    public (string Symbol, long Price) Standard_StringSplit()
    {
        var parts = PayloadText.Split(':');
        var symbol = parts[0];
        long.TryParse(parts[1], out var price);

        return (symbol, price);
    }
}
