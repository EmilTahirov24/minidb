```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12450H 2.00GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  Job-OTIZWT : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=5  RunStrategy=Monitoring  
UnrollFactor=1  WarmupCount=1  

```
| Method   | Engine | Order     | Mean       | Error     | StdDev    |
|--------- |------- |---------- |-----------:|----------:|----------:|
| **LoadKeys** | **MiniDB** | **ascending** |   **199.6 ms** |  **79.82 ms** |  **20.73 ms** |
| **LoadKeys** | **MiniDB** | **random**    | **2,516.1 ms** | **505.52 ms** | **131.28 ms** |
| **LoadKeys** | **SQLite** | **ascending** |   **270.7 ms** |  **23.86 ms** |   **6.20 ms** |
| **LoadKeys** | **SQLite** | **random**    | **3,009.2 ms** | **371.16 ms** |  **96.39 ms** |
