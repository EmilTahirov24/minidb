# Milestone 1: the storage engine

A key-value store on disk: sorted keys in a B+tree of fixed-size pages, changes made durable
through a write-ahead log, and a recovery procedure that brings the database back to exactly
the last acknowledged commit after a crash at any moment. This document is written before the
code; where the code ends up differing, the code is fixed or this document is.

## What milestone 1 is, and is not

It is: ordered keys and values as bytes, `Get`, `Put`, `Delete` and range scans, grouped into
transactions that commit or abort as a whole and survive crashes once committed.

It is not, yet: concurrent writers (milestone 2), SQL and tables (milestone 3), replication
(milestone 4), values larger than about a kilobyte, or access from more than one process.
These are listed again under [known limits](#known-limits) with the reason for each.

## The interface

```csharp
using var db = Database.Open("data/app");      // app.db and app.wal

using (var tx = db.BeginWrite())
{
    tx.Put("user:1"u8, "Aysel"u8);
    tx.Delete("user:2"u8);
    tx.Commit();                                // durable when this returns
}                                               // disposed without Commit: aborted

using (var tx = db.BeginRead())
{
    byte[]? name = tx.Get("user:1"u8);
    foreach (var (key, value) in tx.Scan(from: "user:"u8, to: "user;"u8)) { }
}
```

One write transaction runs at a time, and read transactions wait while it does. That is the
simplest arrangement that is obviously correct, and milestone 2 replaces it: the difference
in throughput is one of the things milestone 2 will measure.

## Files

Two files per database:

- `app.db`, the data file: a sequence of 4 KiB pages. Page 0 is the header; every other page
  is a B+tree node or a free page.
- `app.wal`, the write-ahead log: every page a committed transaction changed, as a whole
  image, appended in order.

A change is durable once its log record is on disk. The data file catches up later, at a
checkpoint, and recovery can always rebuild it from the log.

## Pages

Every page starts with the same header and carries a checksum, so that a page torn by a
crash, or damaged on disk, is recognised rather than trusted:

```
offset  size  field
0       4     checksum     CRC32C of bytes 4..4095
4       1     type         1 header, 2 leaf, 3 internal, 4 free
5       1     reserved
6       2     count        cells in the page
8       2     cell start   where the cell content area begins
10      2     fragmented   bytes lost to deleted cells, recovered by compaction
12      4     right        leaf: next leaf; internal: rightmost child; free: next free page
16      4     left         leaf: previous leaf; otherwise 0
20            slots        count x 2 bytes, offsets of the cells, in key order
...           free space
cell start    cells        grow downwards from the end of the page
```

This is a slotted page: the slots stay sorted and small, and the cells themselves can sit in
any order. Inserting a key moves two-byte slots, never whole records. Deleting one leaves a
hole that is counted in `fragmented`; when a new cell needs more room than the free space in
the middle but less than that plus the holes, the page is compacted first.

A **leaf cell** is `key length, value length, key, value`, the lengths as variable-length
integers. An **internal cell** is `child page, key length, key`, and means: every key in
`child` is smaller than `key`. The page's `right` field holds the child for keys at or above
the last one. Keys compare as unsigned bytes.

A cell may use at most a quarter of a page's usable space, a little over a kilobyte for key
and value together. That bound is what guarantees a split always succeeds: half a page plus
one more cell still fits.

**The header page** (page 0) holds a magic number and format version, the page size, the
root page, the number of pages in use, and the head and length of the free list. It changes
inside transactions like any other page and goes through the log like any other page, so it
needs no separate protection.

## The B+tree

All keys and values live in leaves; internal pages only route. Leaves are linked in both
directions, so a range scan walks sideways without going back up the tree.

**Search** descends from the root: in each internal page, a binary search finds the first
cell whose key is larger than the one wanted, and follows its child, or `right` if there is
none.

**Insert** puts the cell in its leaf. If it does not fit, the leaf splits: a new leaf takes
the upper half of the cells, by bytes rather than by count, the sibling links are updated,
and the first key of the new leaf goes up to the parent as a separator. If the parent is full
in turn, it splits the same way, its middle key moving up; a split of the root makes a new
root and the tree one level taller.

One common case gets its own split. Keys inserted in increasing order - generated ids, most
of all - always land at the end of the last leaf, and a split in half leaves every page half
empty forever. When the new key goes past the end of the rightmost leaf, the split moves only
the new key to the new page. How full pages end up, with and without it, is measured.

**Delete** removes the cell and leaves the page as it is, however empty, unless it is now
completely empty. An empty leaf is unlinked from its neighbours, its entry is removed from its
parent, and the page goes on the free list. An internal page left with a single child is
replaced in its parent by that child; when that page is the root, the tree becomes one level
shorter.

Underfull pages are not merged with their neighbours. Merging and redistributing are where a
B-tree is most often wrong, and PostgreSQL's B-tree index makes the same choice, reclaiming
only pages that become empty. The cost is space after heavy deletion, and it is measured.

Invariants that hold after every transaction, and that a checker in the tests verifies:

1. Keys are strictly increasing, across the whole tree.
2. Every key in the subtree of an internal cell's child is smaller than the cell's key, and at
   least the key of the cell before it.
3. All leaves are at the same depth.
4. Following `right` from the first leaf visits every leaf in key order, and `left` mirrors it.
5. Every internal page has at least one cell. Every leaf has at least one, except a root leaf,
   which is an empty tree.
6. Every page below the page count is exactly one of: the header, reachable from the root, or
   on the free list. No page is lost, none is used twice.
7. Every page's checksum is correct.

## The page cache

Pages are read into a cache of fixed size, a few megabytes by default. Pages in use are
pinned; the rest are evicted with the CLOCK algorithm, which approximates least-recently-used
without reordering a list on every access. A changed page that is evicted before the next
checkpoint is written to the data file first. That is safe because its latest image is
already in the log: if the write is torn by a crash, recovery overwrites it.

## Transactions and the log

A write transaction never changes a page in the cache. The first time it modifies a page, it
takes a private copy, and every later read of that page inside the transaction sees the copy.
Aborting is therefore just dropping the copies.

**Commit**:

1. Append every page the transaction changed to the log, as whole images, the header page
   among them if the tree's shape changed. The last one is marked as the commit.
2. Flush the log to disk (`fsync`).
3. Install the copies in the cache as the current, not yet checkpointed, versions.

`Commit` returns after step 2 and not before: a transaction is durable exactly when its commit
frame is on disk.

If the flush fails, the database stops accepting work and must be reopened, which runs
recovery. Retrying is not safe: PostgreSQL retried a failed `fsync` until 2018, when it was
found that Linux may drop the data that failed to write and report success the second time.

**The log** starts with a header - a magic number, the page size, a generation number, and a
checksum - and then holds frames:

```
offset  size  field
0       4     page         which page this is an image of
4       4     flags        bit 0: this frame commits its transaction
8       8     generation   must equal the log header's
16      4     checksum     CRC32C of this header's first 16 bytes and the image
20      4     reserved
24      4096  image
```

Reading the log from the start, a frame counts only if its checksum is right and its
generation matches; reading stops at the first frame that fails either. A transaction counts
only if its commit frame does. The generation is what makes old frames harmless: resetting
the log writes a new header with the next generation, and every frame of the old one is dead
from then on, without truncating the file.

This way of logging - whole pages, like SQLite in its WAL mode - is not the most economical.
Changing one hundred-byte value writes a four-kilobyte page to the log. It is chosen because
it is the simplest design that stays correct when a crash tears a page in half: whatever state
a page of the data file is in, the log has a whole, checksummed image of it to put back. The
extra bytes per commit are measured.

## Checkpoints

A checkpoint moves the data file forward and empties the log:

1. Write every changed page in the cache to the data file.
2. Flush the data file.
3. Reset the log: write a header with the next generation.

It runs when the log passes a size limit and when the database is closed. A crash anywhere in
it is harmless: until step 3, the log still has every image written since the last complete
checkpoint, and applying them again changes nothing.

## Recovery

Opening a database always runs recovery; after a clean close it has nothing to do.

1. Read the log. Collect the page images of every transaction whose commit frame is there.
2. Write them into the data file in log order, so the latest image of each page wins.
3. Flush the data file, then reset the log.

Applying an image twice gives the same page, so a crash during recovery is repaired by the
next one. If the log's own header is damaged, the log is treated as empty. That is safe
because the header is only ever written by a reset, and a reset only ever follows a flush of
the data file with everything the old log held.

What each kind of crash leaves behind, and why it is repaired:

| Crash during | What is on disk | What recovery does |
| --- | --- | --- |
| a transaction, before commit | Nothing of it: its pages were private copies | Nothing to do |
| writing its frames to the log | Some frames, maybe a torn last one | The commit frame is missing or fails its checksum, so the transaction is ignored; `Commit` never returned |
| the flush of the log | Any part of the frames, in any order | The same: frames after the first bad one are ignored, even if intact |
| writing a page back on eviction | A torn page in the data file | Overwritten by its image from the log |
| a checkpoint | A mix of old and new pages in the data file | Every changed page has an image in the log, which is applied again |
| a reset of the log | A torn log header | The log is treated as empty; the data file already has all of it |
| recovery itself | Part of the images applied | Applied again |

A new database is written to a temporary file and renamed into place, so a crash while it is
being created leaves either no database or a complete one.

## How milestone 1 is checked

All disk access goes through an interface with two implementations: the real file system, and
a simulated disk in memory. The simulated disk keeps what has been flushed apart from what has
only been written, and on a simulated crash it decides what survives: nothing unflushed, all
of it, or a random choice of 512-byte sectors, which is how a page gets torn. Every choice
comes from a seed, so any failure can be replayed exactly.

- **Against a model.** Random sequences of puts, deletes, reads, scans, commits, aborts,
  checkpoints and reopenings, compared after every step with the same operations applied to
  an ordinary sorted dictionary. The checker for the invariants above runs throughout.
- **A crash at every write.** A workload is run once to count its disk operations. It is then
  run again once for every one of them, crashing at that operation, under each of the three
  survival rules. After each crash the database is reopened, the invariant checker runs, and
  the contents must equal the model either as of the last commit that returned or, if a
  commit was under way, as of that one too. Nothing in between is accepted.
- **Damage.** Bits flipped in pages of the data file and the log must be detected by their
  checksums, never read back as data.
- **Benchmarks.** Loading a million keys, in random and in increasing order; random point
  reads; range scans; the latency of single-key commits, which is the latency of an `fsync`;
  the bytes written to the log per byte of data; how full pages are after loading and after
  deleting half the keys. SQLite runs the same workloads as a reference, in WAL mode with full
  synchronisation, in a table keyed by the same bytes.

Milestone 1 is done when the model tests and the crash tests pass across a fixed set of seeds
in CI, with the number of crash points they cover in the README; when the benchmarks are in
the README with the machine and the command that produced them; and when every choice in this
document that was not obvious is in [decisions.md](../decisions.md).

## Order of work

1. The disk interface and the simulated disk; the page format, checksums and the header page.
2. Search, insert and splits, range scans; the model tests and the invariant checker.
3. Delete and the free list; the page cache.
4. The log, commit and abort, checkpoints, recovery; the crash tests.
5. Benchmarks and the README.

## Known limits

- **One writer at a time**, and readers wait for it. Milestone 2 replaces this with
  multi-version concurrency control.
- **About a kilobyte for key and value together.** Larger values would need overflow pages;
  nothing planned needs them.
- **No merging of underfull pages**, as described above.
- **One commit, one `fsync`.** Grouping the commits of concurrent transactions into one flush
  needs concurrent transactions first.
- **One process.** The data file is opened exclusively; a second process gets an error.
- **The directory is not flushed after a new database is renamed into place.** .NET has no call
  for it. A crash in that moment can make a new, empty database disappear; it cannot damage
  one.

The format has a version byte and milestone 2 will change it: versions of a value need a place
to live. This milestone does not try to guess what that place will be.
