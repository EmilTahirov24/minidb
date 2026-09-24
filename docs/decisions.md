# Decisions

Short records of the choices that shaped this project, with the reasoning behind them. A
choice made before the code carries the cost it expects to pay; the measured cost is added
once there is code to measure.

## 1. C# on .NET 10

**Context.** A database is usually written in C, C++, Rust or Go.

**Decision.** C#, on .NET 10.

**Why.** The point of the project is to understand a database from the disk up and to be able
to defend every line of it, which is easier in a language already known well than in one being
learned at the same time. .NET is also a serious place for storage engines: Microsoft's Garnet
and FASTER and the RavenDB database are written in C#. It gives direct control over bytes
(`Span<T>`, `RandomAccess`, hardware CRC32C), and BenchmarkDotNet makes careful measurement
the default rather than an effort.

## 2. The log holds whole page images

**Context.** A write-ahead log can record what happened logically ("put this key"), what
happened to bytes of a page, or whole pages. Some databases avoid a log altogether by never
overwriting a page (LMDB, bbolt).

**Decision.** Every page a transaction changes is appended to the log whole, as in SQLite's WAL
mode, and copied into the data file at checkpoints.

**Why.** It is the simplest design that stays correct when a crash tears a page in the data
file: the log always has an intact image to put back, and putting it back twice changes
nothing, so recovery can itself be interrupted. Logical logging needs the page it replays onto
to be intact, which is exactly what a torn write takes away. The cost is bytes: a small change
logs a whole four-kilobyte page. It is measured in milestone 1.

## 3. Only empty pages are freed

**Context.** Deleting keys leaves B-tree pages partly empty. A textbook B-tree merges or
redistributes them to keep every page at least half full.

**Decision.** Pages are left as they are until they are completely empty; then they are freed.

**Why.** Merging and redistribution are where B-tree implementations are most often wrong, and
the benefit is only space. PostgreSQL's B-tree index makes the same choice. The space lost after
heavy deletion is measured in milestone 1.

## 4. One transaction at a time in milestone 1

**Context.** Concurrent transactions need either locks on individual keys and pages or several
versions of the data.

**Decision.** In milestone 1, one transaction runs at a time, reading or writing, and the others
wait. A thread that already holds one and asks for another gets an exception.

**Why.** It is correct by construction, and it gives milestone 2 something to measure against:
multi-version concurrency control replaces it there, and the difference in throughput shows
what it bought. Readers wait too, not only for the writer, because the page cache is not yet
safe to share between threads. The exception is there because the alternative is worse: a
thread waiting for a turn it holds itself waits for ever, and a test of this project's own did
exactly that before the check existed.

## 5. A failed `fsync` stops the database

**Context.** Writing to disk can fail, and the obvious reaction is to try again.

**Decision.** If flushing the log fails, the database refuses further work until it is reopened
and recovery has run.

**Why.** After a failed flush the operating system may already have dropped the data, and a
second flush can report success anyway. PostgreSQL learned this in 2018 and changed to stopping
too. Recovery from the log is the only state that is known to be right.
