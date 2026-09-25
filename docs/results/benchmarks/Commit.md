```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12450H 2.00GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  Job-YFEFPZ : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3

IterationCount=10  WarmupCount=3  

```
| Method          | Engine | Mean     | Error    | StdDev   |
|---------------- |------- |---------:|---------:|---------:|
| **SingleKeyCommit** | **MiniDB** | **816.8 μs** | **71.07 μs** | **47.01 μs** |
| **SingleKeyCommit** | **SQLite** | **832.3 μs** | **65.41 μs** | **43.26 μs** |
