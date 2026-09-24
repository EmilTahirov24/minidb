# MiniDB

A small database built from the disk up, in C#: a B+tree storage engine with a write-ahead
log, then transactions with multi-version concurrency control, a SQL layer, and replication
with Raft.

The aim is not features but trust. Each guarantee is tested by breaking it on purpose:
crashing after every single write of a workload, tearing pages in half, losing what was never
flushed. Every failure is reproducible from a seed.

**Status:** milestone 1 of 4, the storage engine, is done. The [plan](docs/plan.md) has the
milestones, the [design](docs/design/storage.md) how the storage engine works, and the
[decisions](docs/decisions.md) why it works that way.

## What it does

```csharp
using var db = Database.Open("data/app");   // data/app.db and data/app.wal

using (var tx = db.BeginWrite())
{
    tx.Put("user:1"u8, "Aysel"u8);
    tx.Commit();                             // durable once this returns
}

using (var tx = db.BeginRead())
{
    byte[]? name = tx.Get("user:1"u8);
}
```

Keys and values are bytes, kept in order in a B+tree of 4 KiB pages, each with a checksum. A
transaction's changes become durable through a write-ahead log of whole page images, the way
SQLite's WAL mode does it, and opening a database after a crash brings it back to exactly its
last committed state. For now one transaction runs at a time; milestone 2 changes that.

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

**Against a model.** 360,000 random puts, deletes, reads and range scans, on keys in random,
increasing and decreasing order and of up to 1,000 bytes, each compared with the same
operation on a sorted dictionary.

**Against broken versions of itself.** A test that has never failed is not trusted. Every
group of tests was run against deliberately broken copies of the code it protects - 26 of
them, among them a commit that returns before the log is flushed, a checkpoint that empties
the log before the data file is safe, a transaction replayed without its commit frame, a
stale link between leaves - and every one was caught. Two of those runs found holes in the
tests instead: the test trees were too shallow to reach one deletion path, and a damaged link
made a scan loop for ever rather than fail. Both are fixed.

`dotnet test` runs all of it in under a minute.

## What building it found

Four problems turned up while writing the code, each now covered by a test:

- The design said an internal page left with a single child should be replaced by that
  child. Anywhere below the root, that makes one branch shorter than the others and breaks
  the tree's balance. It was caught before the code for it existed.
- A crash can tear the log's header while the log is being reset, and with it the log's
  generation number. Starting again from zero could make old frames read as the continuation
  of new ones and put stale pages back over newer ones. The next generation is now chosen
  above that of every intact frame in the file.
- A log left behind by a deleted database would have been replayed onto a new one.
- A thread that held a transaction and asked for another waited for itself for ever - one of
  this project's own tests did. It now gets an exception.

## Numbers

A laptop with an Intel Core i5-12450H, Windows 11 and .NET 10. SQLite runs the same work
through Microsoft.Data.Sqlite, in WAL mode with `synchronous=FULL`, the same 4 MiB of cache,
and a `WITHOUT ROWID` table keyed by the same 16-byte keys; values are 100 bytes.

| | MiniDB | SQLite |
| --- | ---: | ---: |
| A commit of one key, which waits for its flush | 829 µs | 822 µs |
| A point read | 8.4 µs | 14.5 µs |
| Reading 100 keys in order | 58 µs | 111 µs |
| Loading 100,000 keys in increasing order, 1,000 per transaction | 179 ms | 330 ms |
| Loading 100,000 keys in random order | 3.2 s | 4.7 s |

- A commit costs what a flush costs, in both. The flush is the work.
- The reads are faster partly for a reason that is not MiniDB's: SQLite is reached through
  ADO.NET, parameter binding and a native call, MiniDB through a method call. A C# program pays
  that difference, but it is not a comparison of B-trees.
- The random loads varied widely between runs, with a standard deviation of 0.6 s for MiniDB
  and 0.8 s for SQLite, so this benchmark cannot tell the two apart there.

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
dotnet run -c Release --project bench/MiniDb.Bench -- measure --out docs/results/storage-measurements.md
dotnet run -c Release --project bench/MiniDb.Bench -- --filter '*' --exporters github
```

The full reports, with the error of every timing, are in [docs/results](docs/results).

## Known limits

- One transaction at a time, readers included, and none held across an `await`.
- About a kilobyte for a key and its value together.
- Underfull pages are not merged; the cost is above.
- One commit, one flush: no grouping of commits.
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
