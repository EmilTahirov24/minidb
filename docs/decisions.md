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
logs a whole four-kilobyte page.

*Measured:* a commit of one new 16-byte key with a 100-byte value writes 4,775 bytes to the
log, of which 2.4% is the data - about 1.16 pages, the leaf and now and then the header page.
SQLite in its WAL mode writes 6,641 bytes for the same commit
([results](results/storage-measurements.md)).

## 3. Only empty pages are freed

**Context.** Deleting keys leaves B-tree pages partly empty. A textbook B-tree merges or
redistributes them to keep every page at least half full.

**Decision.** Pages are left as they are until they are completely empty; then they are freed.

**Why.** Merging and redistribution are where B-tree implementations are most often wrong, and
the benefit is only space. PostgreSQL's B-tree index makes the same choice. The space lost after
heavy deletion is measured in milestone 1.

*Measured:* after deleting a random half of 100,000 keys, not one page is empty, so none is
freed: the data file stays at 17.1 MiB with its pages about a third full. SQLite, which does
rebalance, has 551 pages free for reuse after the same deletes. Random inserts cost space too,
for a related reason: a page that overflows splits in two, so MiniDB's pages end 65% full
after a random load where SQLite's, which spreads the cells over its neighbours, end 82% full
([results](results/storage-measurements.md)). If space matters more than simplicity later,
this is the decision to revisit.

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

*Measured, before milestone 2 replaced it:* with four threads reading while one commits, 285
reads a second, the 99th-percentile read taking 600 ms and the 99.9th 2.4 s. That is worse than
readers waiting their turn: the lock is not fair, the writer takes it again the moment it lets
go, and the readers starve ([results](results/concurrency-milestone-1.md)).

## 5. A failed `fsync` stops the database

**Context.** Writing to disk can fail, and the obvious reaction is to try again.

**Decision.** If flushing the log fails, the database refuses further work until it is reopened
and recovery has run.

**Why.** After a failed flush the operating system may already have dropped the data, and a
second flush can report success anyway. PostgreSQL learned this in 2018 and changed to stopping
too. Recovery from the log is the only state that is known to be right.

## 6. Snapshots come from versions of pages, not of keys

**Context.** Multi-version concurrency control keeps old versions so that a transaction can read
a consistent snapshot while others change the data. The versions can be kept per key, as
PostgreSQL, RocksDB and CockroachDB do, or per page, as LMDB and SQLite's WAL mode do.

**Decision.** Per page, in memory only, for as long as an open transaction may read them.

**Why.** Milestone 1's tree, log, checkpoints and recovery - and the 15,420 recoveries that test
them - stay exactly as they are, and nothing old ever reaches the disk, so nothing has to be
vacuumed. The cost is memory: a transaction that stays open keeps the page versions it might read
until it ends. Versions do not survive a restart, and nothing needs them to.

## 7. Write transactions are optimistic: the first committer wins

**Context.** Concurrent writers either lock what they touch as they go, as two-phase locking
does, or go ahead and are checked when they commit.

**Decision.** A write transaction's changes stay in the transaction until it commits. If another
transaction wrote any of the same keys and committed after this one began, the commit throws
`WriteConflictException` and the caller retries.

**Why.** Nothing ever waits for another transaction, so there is no deadlock to detect; a
transaction that only reads can never fail; and checking the keys written is exactly what
snapshot isolation asks for. The cost is work thrown away when conflicts are frequent, which the
bank-transfer test counts in retries.

*Measured:* four threads moving money among twelve accounts made 1,190 transfers and retried
748 times: conflicts are frequent when every transaction writes two of twelve keys.

## 8. Snapshot isolation, not serializable

**Context.** Snapshot isolation allows two anomalies, write skew and the read-only anomaly, that
serializable isolation forbids.

**Decision.** Snapshot isolation. The two anomalies it allows are documented, and tested as
allowed.

**Why.** It is what PostgreSQL's `REPEATABLE READ` and Oracle's `SERIALIZABLE` give, widely used
and simple to implement correctly. Serializable snapshot isolation, which PostgreSQL has used for
`SERIALIZABLE` since 9.1, adds tracking of what each transaction reads; it is a possible later
step, not part of this milestone.
