```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12450H 2.00GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  Job-YFEFPZ : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3

IterationCount=10  WarmupCount=3  

```
| Method    | Engine | Mean      | Error     | StdDev    |
|---------- |------- |----------:|----------:|----------:|
| **PointRead** | **MiniDB** |  **6.074 μs** | **0.1135 μs** | **0.0676 μs** |
| Scan100   | MiniDB | 49.497 μs | 5.7459 μs | 3.8006 μs |
| **PointRead** | **SQLite** |  **6.833 μs** | **0.1114 μs** | **0.0663 μs** |
| Scan100   | SQLite | 69.263 μs | 1.4347 μs | 0.8537 μs |
