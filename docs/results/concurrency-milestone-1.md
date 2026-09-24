4 threads reading random keys and 1 thread committing single-key updates, together, for 3 s; 100,000 keys of 16 bytes, values of 100. The median of 5 runs.

| Reads per second | Commits per second | Read, median | Read, 99th percentile | Read, 99.9th percentile |
|---|---|---|---|---|
| 285 | 1,238 | 8.0 µs | 599,540.0 µs | 2,425,022.1 µs |
