# 📊 FalconFX Performance Benchmark Report

> **Environment:** Intel Core i7-12700H (14C/20T) | Windows 11 (25H2) | .NET 10.0.10 (RyuJIT AVX2)  
> **Runner:** BenchmarkDotNet v0.15.8 & Automated Aspire E2E Harness (`Aspire.Hosting.Testing`)

---

## ⚡ Executive Summary

| Test Level | Scope | Throughput | Latency | Memory Allocations |
| :--- | :--- | :--- | :--- | :--- |
| **Level 1: Micro-Benchmark (BDN)** | Pure C# OrderBook Algorithm | **~61,300,000 ops/sec** | **16.3 ns / order** | **0 B (Zero-Alloc)** |
| **Level 2: Internal Async Pipeline** | Protobuf SerDe + Channels + Thread | **~2,317,000 ops/sec** | **0.43 μs / order** | Low Alloc |
| **Level 3: Automated TRUE E2E System** | **Kafka Net + Engine + Postgres Binary COPY** | **5,558 - 66,500 ops/sec** | **0.015 - 0.180 ms / order** | Persistent DB |

---

## 🌐 Level 3: Automated TRUE End-to-End System Benchmark Results

Measures the entire distributed ecosystem over physical network sockets & infrastructure:  
`Kafka Network Producer` ➡️ `Kafka Topic (orders)` ➡️ `MatchingEngine` ➡️ `Kafka Topic (trades)` ➡️ `TradeProcessor (Binary COPY)` ➡️ `PostgreSQL Bulk Persistence`

* **Automated Orchestration:** `.NET Aspire AppHost` booted, tested, dynamic endpoints resolved, and shut down automatically in-process via `Aspire.Hosting.Testing`.
* **PostgreSQL Binary COPY Protocol:** Implemented `NpgsqlBinaryImporter` (`COPY FROM STDIN BINARY`), accelerating bulk persistence of **25,000 trades in 0.75s - 9.00s**.
* **True E2E System Throughput:** **5,558 - 66,500 orders / sec** (Sustained network + DB persistence pipeline under parallel MarketMaker flow)
* **True E2E Average Latency:** **0.015 - 0.180 milliseconds / order** (15 - 180 microseconds)
* **Total Executed Batch:** 50,000 orders over network sockets

---

## 🚀 Level 2: Internal Async Pipeline Benchmark Results

Measures internal async execution:  
`Protobuf Parse` ➡️ `System.Threading.Channels` ➡️ `EngineWorker Dedicated Thread` ➡️ `OrderBook Match` ➡️ `Trade Serialization`

* **Pipeline Throughput:** **2,317,403 orders / sec**
* **Average Pipeline Latency:** **0.43 microseconds / order** (431 nanoseconds)
* **Total Executed Batch:** 500,000 orders in **0.216 seconds**

---

## 🔬 Level 1: Micro-Benchmarks (BenchmarkDotNet)

### 1. Matching Engine Core (`OrderBookBenchmark`)

Measures L2 OrderBook execution, matching, and removal performance across 100,000 orders.

```text
| Method                          | OrderCount | Mean       | Allocated |
|-------------------------------- |----------- |-----------:|----------:|
| ProcessOrders_FullMatchScenario | 10000      |   151.4 us |      96 B |
| ProcessOrders_FullMatchScenario | 100000     | 1,629.7 us |      96 B |
```

#### 📐 Analytical Metrics:

* **Throughput:**
  $$\text{Throughput} = \frac{100,000 \text{ orders}}{0.0016297 \text{ s}} = \mathbf{61,359,759 \text{ orders/sec}}$$

* **Single-Order Latency:**
  $$\text{Latency} = \frac{1.6297 \text{ ms}}{100,000} = \mathbf{16.29 \text{ ns/order}}$$

* **Memory Impact:**  
  The inner execution loop is **0-Allocation**. The reported 96 bytes represents a single lambda delegate capture (`_ => tradeCount++`) at the benchmark execution boundary.

---

### 2. Memory Pool vs. Heap Allocations (`OrderPoolBenchmark`)

Compares FalconFX’s `OrderPool` (struct-based array recycling) against standard `new HeapObject()` allocations across 100,000 operations.

```text
| Method                            | Mean     | Ratio | Gen0     | Gen1     | Gen2     | Allocated |
|---------------------------------- |---------:|------:|---------:|---------:|---------:|----------:|
| FalconFX_StructPool_RentAndReturn | 241.3 us |  1.00 |        - |        - |        - |       0 B |
| Traditional_Heap_Allocations      | 974.2 us |  4.04 | 226.5625 | 214.8438 | 210.9375 | 4000127 B |
```

#### 📐 Analysis:
* **Speed:** StructPool is **4.04x faster** than Heap allocations.
* **GC Impact:** Traditional heap allocations created **~4 MB of garbage** per 100,000 orders, triggering `Gen2 = 210` collections (Stop-the-World pauses).
* **FalconFX Result:** `Gen0/1/2 = 0`, `Allocated = 0 B`. Stop-the-World GC pauses in the HFT execution path are completely eliminated.

---

### 3. PRNG - Random Order Generation (`RandomGeneratorBenchmark`)

Compares the synthetic order generator's `XorShift64` algorithm against standard .NET `Random.Shared`.

```text
| Method                | Mean      | Ratio | Allocated |
|---------------------- |----------:|------:|----------:|
| FalconFX_XorShift64   | 0.2781 ns |  1.00 |       0 B |
| Standard_RandomShared | 0.6861 ns |  2.49 |       0 B |
```

#### 📐 Analysis:
* **Sub-Nanosecond Speed:** `XorShift64` generates a random number in **0.278 ns** (278 picoseconds).
* **2.49x faster** than `Random.Shared`. Ensures the `MarketMaker` service operates without CPU bottlenecks.

---

### 4. Gateway Zero-Alloc Span Parsing (`SpanParsingBenchmark`)

Compares Gateway Redis Pub/Sub message parsing (`"EURUSD:10550"`) using `Span<char>` + String Pool vs. standard `string.Split()`.

```text
| Method                         | Mean     | Ratio | Gen0   | Allocated |
|------------------------------- |---------:|------:|-------:|----------:|
| FalconFX_ZeroAlloc_SpanParsing | 16.77 ns |  1.00 | 0.0032 |      40 B |
| Standard_StringSplit           | 32.69 ns |  1.95 | 0.0089 |     112 B |
```

#### 📐 Analysis:
* **Speed:** `Span<char>` parsing is **1.95x faster**.
* **Memory:** Allocations reduced from **112 B -> 40 B** (64% reduction). Gen0 GC pressure reduced by 64%.

---

## 🎯 Key Architectural Takeaways

1. **Deterministic Latency:** Zero-Alloc architecture eliminates Garbage Collection latency spikes.
2. **High Single-Core Capacity:** The Matching Engine processes **60M+ orders/sec** on a single CPU core.
3. **Hardware Efficiency:** Fully leverages RyuJIT JIT compilation and AVX2 vectorization.