# Readers and a writer at the same time

Four threads read random keys while one thread commits single-key updates, together, for 3 s;
100,000 keys of 16 bytes, values of 100; each line is the median of 5 runs. Milestone 1 (commit
`5825a8a`, one transaction at a time) and milestone 2 (commit `0028251`, snapshot isolation)
were run alternately in one session, twice, so that both see the same machine: on this laptop,
the same benchmark can measure up to twice as fast in one session as in another.

| Run | Code | Reads per second | Commits per second | Read, median | Read, 99th percentile | Read, 99.9th percentile |
|---|---|---|---|---|---|---|
| 1 | milestone 1 | 315 | 938 | 13.4 µs | 40,287.8 µs | 3,002,118.1 µs |
| 1 | milestone 2 | 74,955 | 958 | 20.1 µs | 312.7 µs | 1,533.0 µs |
| 2 | milestone 1 | 351 | 952 | 9.1 µs | 338.5 µs | 3,006,133.7 µs |
| 2 | milestone 2 | 74,672 | 949 | 20.2 µs | 315.2 µs | 1,528.3 µs |

Under milestone 1 the slowest reads wait the whole three seconds: the lock every transaction
took is not fair, and the committing thread takes it straight back. Under milestone 2 no read
waits for the writer, commits run at the same rate, and a typical read takes longer because
four readers now really run at once and share the cache's lock.

```bash
git worktree add ../minidb-m1 5825a8a
cd ../minidb-m1 && dotnet run -c Release --project bench/MiniDb.Bench -- concurrency
cd - && dotnet run -c Release --project bench/MiniDb.Bench -- concurrency
```
