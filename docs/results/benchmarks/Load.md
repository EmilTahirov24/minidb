```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12450H 2.00GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  Job-OTIZWT : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=5  RunStrategy=Monitoring  
UnrollFactor=1  WarmupCount=1  

```
| Method   | Engine | Order     | Mean       | Error       | StdDev    |
|--------- |------- |---------- |-----------:|------------:|----------:|
| **LoadKeys** | **MiniDB** | **ascending** |   **178.7 ms** |    **94.10 ms** |  **24.44 ms** |
| **LoadKeys** | **MiniDB** | **random**    | **3,184.8 ms** | **2,167.73 ms** | **562.95 ms** |
| **LoadKeys** | **SQLite** | **ascending** |   **329.5 ms** |   **266.48 ms** |  **69.20 ms** |
| **LoadKeys** | **SQLite** | **random**    | **4,692.1 ms** | **3,161.73 ms** | **821.09 ms** |
