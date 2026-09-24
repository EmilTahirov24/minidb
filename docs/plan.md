# Plan

MiniDB is a database built from the disk up: a storage engine, then transactions, then SQL,
then replication. Each milestone ends as a complete system that works and is tested on its
own, so the project is worth something at every stage. If time runs short, milestones are
cut from the end, never the testing inside them.

## What makes it more than a toy

Most small databases are tested by using them. This one is tested by breaking it. Every
guarantee is checked against a fault that would violate it if the code were wrong: a disk
that loses writes it was not told to keep and tears pages in half, a crash after every single
write of a workload, a network that drops, delays and partitions messages. Every failure is
reproducible from a seed, and every claim in the README comes with the number of faults it
survived.

## Milestones

| # | Milestone | How it is checked | Dates |
| --- | --- | --- | --- |
| 1 | **Storage engine**: B+tree over fixed-size pages, a page cache, a write-ahead log, crash recovery | Random operations against an in-memory model; a crash at every write of a workload, with torn pages; benchmarks against SQLite | Sep 25 – Oct 19 |
| 2 | **Transactions**: multi-version concurrency control, snapshot isolation | One test per known anomaly (dirty read, lost update, write skew, …) stating what snapshot isolation allows; concurrent bank transfers that must never change the total | Oct 20 – Nov 9 |
| 3 | **SQL**: parser, planner and executor over tables and secondary indexes | A subset of SQLite's own sqllogictest suite; random queries whose results must match SQLite's | Nov 10 – Nov 30 |
| 4 | **Replication**: Raft across three nodes | Deterministic simulation of the whole cluster with crashes, delays and partitions; every history checked for linearizability | Dec 1 – Dec 28 |

Milestone 4 is the one to cut if needed; the first three stand on their own as a
single-node database.

## Every milestone ends with

- the code, with its tests running in CI on Linux and Windows;
- a README section saying what works, what does not, and the numbers, each with the command
  that produced it;
- entries in [decisions.md](decisions.md) for every choice that was not obvious;
- a design document in `docs/design/`, written before the code.

## Rules

- A number in the README comes from a run of a benchmark or a test. If a command cannot
  reproduce it, it does not belong there.
- A known limitation is written down, not left to be discovered.
- Tests name the behaviour they protect, and a test that has never failed on a broken version
  of the code is not trusted until it has.
