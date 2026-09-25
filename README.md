# MiniDB

A small database built from the disk up, in C#: a B+tree storage engine with a write-ahead
log, then transactions with multi-version concurrency control, a SQL layer, and replication
with Raft.

The aim is not features but trust. Each guarantee is tested by breaking it on purpose:
crashing after every single write of a workload, tearing pages in half, losing what was never
flushed, running transactions in every order a model allows. Every failure is reproducible
from a seed.

**Status:** milestones 1 and 2 of 4 - the storage engine, and transactions that run side by
side - are done. The [plan](docs/plan.md) has the milestones, the designs
([storage](docs/design/storage.md), [transactions](docs/design/transactions.md)) how it works,
and the [decisions](docs/decisions.md) why it works that way.

## What it does

```csharp
using var db = Database.Open("data/app");   // data/app.db and data/app.wal

using (var tx = db.BeginWrite())
{
    tx.Put("user:1"u8, "Aysel"u8);
    tx.Commit();                             // durable once this returns
}

using (var tx = db.BeginRead())              // never waits for a writer
{
    byte[]? name = tx.Get("user:1"u8);
}
```

Keys and values are bytes, kept in order in a B+tree of 4 KiB pages, each with a checksum. A
transaction's changes become durable through a write-ahead log of whole page images, the way
SQLite's WAL mode does it, and opening a database after a crash brings it back to exactly its
last committed state.

Any number of transactions run at once under snapshot isolation. Each sees the database as of
the last commit before it began; reading never waits for anything. A write transaction keeps
its changes to itself until it commits, and if another transaction wrote one of the same keys
and committed first, its commit throws `WriteConflictException` and writes nothing - the
caller retries. Snapshot isolation allows write skew and the read-only anomaly, and MiniDB does
too; both are documented and tested as allowed.

## How it is tested

**A crash at every write.** The tests run the database on a simulated disk that keeps what
was flushed apart from what was only written. Twelve workloads of 60 transactions each are run
once to count their disk operations, then again for every one of those operations, crashing
there: 3,855 crash points. After each crash the disk comes back four ways - none of the
unflushed writes survived, all of them did, or a random choice of 512-byte sectors did, twice,
which is how pages get torn. That makes 15,420 recoveries, and after each one the tree's shape
is checked, every page is accounted for, the contents must equal either the last committed
state or the one that was being committed - nothing in between - and the database must still
take a commit afterwards. Another 1,004 crashes land in the middle of a recovery, and the next
recovery has to repair them.

**Against models.** 360,000 random puts, deletes, reads and range scans, on keys in random,
increasing and decreasing order and of up to 1,000 bytes, each compared with the same
operation on a sorted dictionary. And 80,000 operations of up to four transactions interleaved
at random, compared with a small reference implementation of snapshot isolation that keeps
every committed version of every key: every read, scan and commit must come out the same.

**Every anomaly, written out.** One test per anomaly, each an exact interleaving of
transactions: dirty reads, non-repeatable reads, phantoms and lost updates must not happen;
write skew and the read-only anomaly must.

**Real threads.** Four threads move money between twelve accounts, retrying on conflict,
while two others add up every balance: 1,190 transfers, 748 retries, and 10,053 sums, every
one of them equal to the money the bank started with.

**Against broken versions of itself.** A test that has never failed is not trusted. Every
group of tests was run against deliberately broken copies of the code it protects - 35 of
them, among them a commit that returns before the log is flushed, a checkpoint that empties
the log before the data file is safe, a transaction replayed without its commit frame, a
commit never checked for conflicts, a page version dropped while a snapshot still needs it -
and every one was caught. Three of those runs found holes in the tests instead: the test trees
were too shallow to reach one deletion path, a damaged link made a scan loop for ever rather
than fail, and an exception on a test's own thread ended the whole test run rather than
failing the test. All three are fixed.

**What that does not cover.** Crashing once per operation, and once more during the recovery,
does not produce every sequence of crashes. The log-generation problem below needs two crashes
in a particular order: with its fix removed, every crash test still passes, and only the test
written for that case fails.

`dotnet test` runs all 102 tests in under a minute.

## What building it found

- The storage design said an internal page left with a single child should be replaced by that
  child. Anywhere below the root, that makes one branch shorter than the others and breaks the
  tree's balance. It was caught before the code for it existed.
- A crash can tear the log's header while the log is being reset, and with it the log's
  generation number. Starting again from zero could make old frames read as the continuation
  of new ones and put stale pages back over newer ones. The next generation is now chosen above
  that of every intact frame in the file.
- A log left behind by a deleted database would have been replayed onto a new one.
- One transaction at a time was worse than it sounded. Measured before milestone 2 changed
  anything, a writer committing in a loop starved the readers: the lock was not fair, and the
  slowest reads waited seconds.
- In the transactions design, a commit publishing new page versions between a transaction
  reading the last commit number and registering itself would have dropped versions the
  transaction needed. Both now happen under one lock; this was designed out before the code.

## Numbers

A laptop with an Intel Core i5-12450H, Windows 11 and .NET 10. On this machine the same
benchmark can come out up to twice as fast in one session as in another, so every comparison
below was made within one session, and a change between versions is only reported after running
both versions alternately. Two apparent regressions of milestone 2 - loads five times slower,
commits 31% fewer - disappeared that way.

**Readers and a writer at the same time**, four threads reading random keys while one commits
single-key updates, the two milestones alternately:

| | Milestone 1 | Milestone 2 |
| --- | ---: | ---: |
| Reads per second | 315 and 351 | 74,955 and 74,672 |
| The slowest 0.1% of reads | 3.0 s | 1.5 ms |
| Commits per second | 938 and 952 | 958 and 949 |
| A typical read | 9 to 13 µs | 20 µs |

Under milestone 1 the slowest reads wait out the whole three-second run. Under milestone 2 no
read waits for the writer and commits keep their rate; a typical read takes longer because four
readers now really do run at once and share the cache's lock.

**One thread at a time, against SQLite.** SQLite runs the same work through
Microsoft.Data.Sqlite, in WAL mode with `synchronous=FULL`, the same 4 MiB of cache, and a
`WITHOUT ROWID` table keyed by the same 16-byte keys; values are 100 bytes.

| | MiniDB | SQLite |
| --- | ---: | ---: |
| A commit of one key, which waits for its flush | 817 µs | 832 µs |
| A point read | 6.1 µs | 6.8 µs |
| Reading 100 keys in order | 49 µs | 69 µs |
| Loading 100,000 keys in increasing order, 1,000 per transaction | 200 ms | 271 ms |
| Loading 100,000 keys in random order | 2.5 s | 3.0 s |

- A commit costs what a flush costs, in both. The flush is the work.
- The reads are faster partly for a reason that is not MiniDB's: SQLite is reached through
  ADO.NET, parameter binding and a native call, MiniDB through a method call. A C# program pays
  that difference, but it is not a comparison of B-trees.
- The random loads come with error margins of about half a second each, and those overlap:
  this run cannot call the difference.

What the design costs in space, counted rather than timed:

| | MiniDB | SQLite |
| --- | ---: | ---: |
| Written to the log by a commit of one key | 4,775 bytes | 6,641 bytes |
| Data file after 100,000 keys in increasing order | 12.0 MiB, 92% data | 13.5 MiB, 82% data |
| Data file after 100,000 keys in random order | 17.1 MiB, 65% data | 13.4 MiB, 82% data |
| Pages freed by deleting a random half of those | 0 | 551 |

- Keys in increasing order fill MiniDB's pages because the last leaf, when it fills up, keeps
  its keys and starts a new page, instead of splitting in half.
- Random keys fill them only to 65%: a page that overflows splits in two, where SQLite spreads
  the cells over the page's neighbours.
- Deleting half the keys at random frees nothing, because almost no page becomes completely
  empty, and only empty pages are freed. That is the measured cost of
  [decision 3](docs/decisions.md).

```bash
dotnet test
dotnet run -c Release --project bench/MiniDb.Bench -- concurrency
dotnet run -c Release --project bench/MiniDb.Bench -- measure --out docs/results/storage-measurements.md
dotnet run -c Release --project bench/MiniDb.Bench -- --filter '*' --exporters github
```

The full reports, with the error of every timing, are in [docs/results](docs/results).

## Known limits

- Snapshot isolation, not serializable: write skew and the read-only anomaly can happen.
- Commits go one at a time, one flush each; grouping them is the obvious next step.
- A write transaction's changes must fit in memory, and a transaction left open keeps the old
  page versions it might read in memory until it ends.
- About a kilobyte for a key and its value together.
- Underfull pages are not merged; the cost is above.
- One process at a time.
- The directory is not flushed after a new database is created, as .NET has no call for it: a
  crash at that moment can lose the new, empty database, never damage one.

## Build

```bash
dotnet build
dotnet test
```

Needs the .NET 10 SDK.

## License

MIT
