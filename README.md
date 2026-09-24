# MiniDB

A small database built from the disk up, in C#: a B+tree storage engine with a write-ahead
log, then transactions with multi-version concurrency control, a SQL layer, and replication
with Raft.

The aim is not features but trust. Each guarantee is to be tested by breaking it on purpose:
crashing after every single write of a workload, tearing pages in half, losing what was never
flushed, cutting the network between nodes. Every failure is reproducible from a seed.

**Status:** designing milestone 1 of 4, the storage engine. Nothing is implemented yet. The
design is in [docs/design/storage.md](docs/design/storage.md), the plan and dates in
[docs/plan.md](docs/plan.md), and the reasoning behind each choice in
[docs/decisions.md](docs/decisions.md).

## Build

```bash
dotnet build
dotnet test
```

Needs the .NET 10 SDK.

## License

MIT
