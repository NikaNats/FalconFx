```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7309/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12700H 2.30GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.0 (10.0.0, 10.0.25.52411), X64 RyuJIT x86-64-v3


```
| Method          | Mean     | Error     | StdDev    | Median   | Allocated |
|---------------- |---------:|----------:|----------:|---------:|----------:|
| Match1000Orders | 2.965 ms | 0.1014 ms | 0.2989 ms | 2.844 ms |         - |
