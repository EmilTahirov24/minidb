```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12450H 2.00GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  Job-YFEFPZ : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3

IterationCount=10  WarmupCount=3  

```
| Method    | Engine | Mean       | Error     | StdDev    |
|---------- |------- |-----------:|----------:|----------:|
| **PointRead** | **MiniDB** |   **8.416 μs** | **0.5402 μs** | **0.3573 μs** |
| Scan100   | MiniDB |  58.079 μs | 4.2478 μs | 2.8097 μs |
| **PointRead** | **SQLite** |  **14.544 μs** | **0.3483 μs** | **0.2072 μs** |
| Scan100   | SQLite | 111.490 μs | 4.8625 μs | 3.2162 μs |
