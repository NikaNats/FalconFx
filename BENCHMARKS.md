# 📊 FalconFX Performance Benchmark Report

> **Environment:** Intel Core i7-12700H (14C/20T) | Windows 11 (25H2) | .NET 10.0.10 (RyuJIT AVX2)  
> **Runner:** BenchmarkDotNet v0.15.8, Automated Aspire E2E Harness (`Aspire.Hosting.Testing`) & High-Precision HFT Metrics Engine (`Stopwatch` QPC)

---

## ⚡ Executive Summary

| Test Level | Scope | Throughput | Time / Order | Memory Allocations |
| :--- | :--- | :--- | :--- | :--- |
| **Level 0: Advanced HFT Engine** | **Tick-to-Trade + Risk Check + CPU Pinning** | **~68,500,000 ops/sec** | **14.6 ns / order (amortized)** | **0 B (Zero-Alloc)** |
| **Level 1: Micro-Benchmark (BDN)** | Pure C# OrderBook Algorithm | **~61,300,000 ops/sec** | **16.3 ns / order (amortized)** | **0 B (Zero-Alloc)** |
| **Level 2: Internal Async Pipeline** | Protobuf SerDe + Channels + Thread | **~2,891,000 ops/sec** | **0.35 μs / order (amortized)** | Low Alloc |
| **Level 3: Automated TRUE E2E System** | **Kafka Net + Engine + Postgres Binary COPY** | **~39,700 ops/sec** (5,558 - 66,500 observed) | **0.025 ms / order** (inverse throughput) | Persistent DB |

> **Reading these numbers:** "Time / order" for Levels 0, 2 and 3 is *amortized* (elapsed wall time divided by the number of orders), i.e. the inverse of throughput. It is not a per-order request latency. Genuine per-trade latency for the E2E system is reported separately as p50/p95/p99 of `InsertedAt - trade.Timestamp` queried from PostgreSQL. `InsertedAt` is stamped by `TradeProcessor` when the trade is deserialized from Kafka (before the binary `COPY`), so the metric covers engine → Kafka → processor ingest and **excludes** the final COPY/commit duration. Run-to-run variance is dominated by Docker host CPU/I/O contention.

---

## 🎯 Level 0: Advanced HFT Micro-Latency & Tail Percentile Analysis

Measures single-core end-to-end execution dynamics under CPU Thread Pinning (Core #2):  
`Sequence Validation` ➡️ `Pre-Trade Risk Check` ➡️ `OrderBook Match` ➡️ `Trade Callback Execution`

> **Methodology:** individual operations complete in far less than the ~100 ns `Stopwatch` resolution, so each timed sample covers **1,000 operations**. The percentiles below are therefore percentiles of *per-operation averages within a batch*, not of single calls. The current native thread is pinned with `SetThreadAffinityMask` (Windows); the pin stays in effect for the process lifetime.

### 1. Pre-Trade Risk Check Micro-Benchmark (< 1 μs Target)
* **Sample Batch Size:** 1,000,000 Orders (1,000 batches of 1,000 ops)
* **Mean Risk Check Time:** **10.68 nanoseconds (0.0107 μs) / order**
* **p50 (Median):** **1.20 ns**
* **p99:** **6.20 ns**
* **p99.999 (Tail):** **8,650.00 ns (8.65 μs)**
* **Memory Allocation:** **0 B (Zero-Alloc Stack Execution)**

---

### 2. Tick-to-Trade Latency & Tail Percentile Breakdown
* **Single-Core Throughput (MPS):** **68,501,127 orders/sec**
* **Total Processed Orders / Trades:** 1,000,000 / 1,000,000
* **Sequence Gaps / Drop Rate:** **0 Drops (0% Message Loss)**

```text
=======================================================================
LATENCY PERCENTILE BREAKDOWN (Tick-to-Trade, per-operation):
  Min Latency:             13.20 ns (0.013 μs)
  Mean Latency:            14.60 ns (0.015 μs)
  p50 (Median):            13.60 ns (0.014 μs)
  p90:                     15.00 ns (0.015 μs)
  p99:                     38.20 ns (0.038 μs)
  p99.9:                   254.00 ns (0.254 μs)
  p99.99:                  254.00 ns (0.254 μs)
  p99.999 (Tail Latency):  254.00 ns (0.254 μs)
  Max Spike:               254.00 ns (0.254 μs)
  Jitter (Std Dev):        ±8.82 ns (0.009 μs)
=======================================================================
```

#### 📐 Analytical Metrics:
* **Sub-Microsecond Execution:** 99.9% of batch-averaged orders process in **<= 254 nanoseconds**.
* **Zero Jitter Boundary:** Up to `p99.9`, execution stays within ~0.25 μs without Garbage Collector interruption.

---

## 🌐 Level 3: Automated TRUE End-to-End System Benchmark Results

Measures the entire distributed ecosystem over physical network sockets & infrastructure:  
`Kafka Network Producer` ➡️ `Kafka Topic (orders)` ➡️ `MatchingEngine` ➡️ `Kafka Topic (trades)` ➡️ `TradeProcessor (Binary COPY)` ➡️ `PostgreSQL Bulk Persistence`

* **Automated Orchestration:** `.NET Aspire AppHost` booted, tested, dynamic endpoints resolved, and shut down automatically in-process via `Aspire.Hosting.Testing`.
* **PostgreSQL Binary COPY Protocol:** Implemented `NpgsqlBinaryImporter` (`COPY FROM STDIN BINARY`), persisting **25,000 trades** in a representative clean run (1.26 s total pipeline time, including Kafka produce/flush and consumer wait).
* **True E2E System Throughput:** **39,685 orders / sec** in a representative clean run (observed range **5,558 - 66,500 orders / sec** across runs and host I/O conditions); the benchmark runs with the continuous MarketMaker disabled so it measures a controlled 50,000-order burst.
* **Amortized Time / Order:** **0.025 milliseconds / order** (25 microseconds) — this is the inverse of throughput, **not** a per-order latency
* **Trade → DB Latency Percentiles (representative clean run, from PostgreSQL):** **p50 13.247 ms | p95 31.165 ms | p99 33.895 ms**, computed as `InsertedAt - to_timestamp(trade.Timestamp)` over the persisted rows. `InsertedAt` is the processor's ingest timestamp, so this spans engine → Kafka → `TradeProcessor` and excludes the final COPY/commit. Host CPU/I/O contention can move these numbers substantially between runs.
* **Total Executed Batch:** 50,000 orders over network sockets (25,000 executed trades, alternating buy/sell at the same price)

---

## 🚀 Level 2: Internal Async Pipeline Benchmark Results

Measures internal async execution:  
`Protobuf Parse` ➡️ `System.Threading.Channels` ➡️ `EngineWorker Dedicated Thread` ➡️ `OrderBook Match` ➡️ `Trade Serialization`

* **Pipeline Throughput:** **2,891,382 orders / sec**
* **Amortized Time / Order:** **0.35 microseconds / order** (346 nanoseconds, inverse throughput)
* **Total Executed Batch:** 500,000 orders in **0.173 seconds**

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
2. **High Single-Core Capacity:** The Matching Engine processes **68.5M+ orders/sec** with full Pre-Trade Risk Checks and Sequence Control on a single CPU core.
3. **Hardware Efficiency:** Fully leverages RyuJIT JIT compilation, AVX2 vectorization, and CPU Thread Pinning.

---

## 🛠️ Reproducing These Results

```bash
# Level 0 — HFT tick-to-trade, risk check, percentiles (no infrastructure needed)
dotnet run -c Release --project tests/FalconFX.Benchmarks -- --hft

# Level 2 — internal Protobuf + Channels pipeline
dotnet run -c Release --project tests/FalconFX.Benchmarks -- --real

# Level 3 — full Aspire stack: Kafka + MatchingEngine + TradeProcessor + PostgreSQL
# (requires Docker; boots and tears down the AppHost in-process)
dotnet run -c Release --project tests/FalconFX.Benchmarks -- --e2e

# Level 1 — BenchmarkDotNet micro-benchmarks
dotnet run -c Release --project tests/FalconFX.Benchmarks
```

> Never compare runs taken while other workloads (Docker builds, test suites, browsers) are active — CPU contention moves the tail percentiles far more than the median. Always report the machine, the .NET version and the benchmark commit alongside the numbers.