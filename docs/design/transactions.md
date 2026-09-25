# Milestone 2: transactions that run at the same time

Milestone 1 ran one transaction at a time. Measured with four threads reading while one
commits, that meant 285 reads a second, and the slowest reads waiting 0.6 to 2.4 seconds: the
single lock is not fair, the writer takes it again the moment it lets go, and the readers
starve ([results](../results/concurrency-milestone-1.md)). Milestone 2 lets any number of
transactions run at once. Each sees a consistent snapshot of the database, readers never wait,
and writers only get in each other's way when they write the same key. This document is written
before the code; where the code ends up differing, the code is fixed or this document is.

## What milestone 2 is, and is not

It is: snapshot isolation. Readers never wait for writers or for each other. Write transactions
run side by side and are checked against each other only when they commit, and on a key both
have written, the first to commit wins.

It is not, yet: serializable isolation - snapshot isolation allows write skew, described below;
commits in parallel - they still go one at a time, one flush each; transactions larger than
memory; or versions that outlive the process. Old versions exist only for transactions that are
open, in memory. After a restart only the latest committed state exists, and it is all anyone
can then see.

## What a transaction sees

A transaction sees the database as of the last commit before it began, with its own changes on
top. Nothing committed after it began is visible to it, and nothing it writes is visible to
anyone until it commits.

| Anomaly | What it is | Under snapshot isolation |
| --- | --- | --- |
| Dirty read | Reading a change that is never committed | Cannot happen |
| Non-repeatable read | Reading one key twice and getting two values | Cannot happen |
| Phantom | Scanning one range twice and getting different keys | Cannot happen |
| Lost update | Two transactions read a value, and both write back something computed from it | Prevented: the second commit fails |
| Write skew | Two transactions each read what the other writes, and each keeps a rule the other's write breaks | Can happen |
| Read-only anomaly | A transaction that only reads sees a state that no order of the others would produce | Can happen |

Write skew is the classic example: a rule that at least one of two doctors is on call, and two
transactions that each check the other doctor is on call and take themselves off. Each commit is
fine on its own snapshot; together they break the rule. The read-only anomaly (Fekete, O'Neil and
O'Neil, 2004) is subtler: a report that reads two accounts while two other transactions run can
see a combination that no serial order of those transactions would have shown. Both are what
snapshot isolation is, not bugs of this implementation, and the tests pin them as allowed, so a
change that makes them impossible is noticed as much as one that breaks the rest.

## Snapshots from page versions

Every commit gets the next sequence number. A transaction notes the last number when it begins,
and when it reads a page it gets the newest version of that page at or below its number.

So the cache keeps, for a page that changed while some older transaction was open, the versions
that transaction may still read. When transactions end, versions that no open transaction can
see any more are dropped. A page that is not in memory at all is read from the data file, and
that is always right: it holds the newest version, and a page is only ever evicted when no open
transaction could need an older one - a page with older versions still kept is never evicted.

The alternative is versions of each key: PostgreSQL keeps old rows in the table with the
transactions that created and deleted them, and RocksDB and CockroachDB suffix every key with a
timestamp. That puts versions on disk, in the tree and in the log, and needs a process to clear
old ones away. Versions of pages leave all of milestone 1 as it is - the tree, the log, the
checkpoints, recovery and every test of them - and nothing old ever reaches the disk, so there is
nothing to vacuum. The price is memory: a transaction that stays open keeps the versions it
might read in memory for as long as it does.

## Write transactions

A write transaction's changes stay in the transaction until it commits: a sorted set of keys
with their new values, a deletion recorded as such. Reading a key looks there first and then in
the snapshot, and a scan merges the two in key order. Nothing reaches the tree before the commit,
so no one can see an uncommitted change, and abandoning a transaction costs nothing.

## Commit: the first committer wins

Commits go one at a time. A commit:

1. Checks every key it writes: if another transaction wrote that key and committed after this
   one began, the commit throws `WriteConflictException` and changes nothing. The caller can
   retry the whole transaction.
2. Applies its changes to the newest version of the tree, with milestone 1's machinery: private
   copies of the pages it changes.
3. Appends those pages to the log and flushes it. From here it is durable.
4. Publishes the new page versions under the next sequence number, and records, for every key it
   wrote, that it was written at that number.

The record of who wrote which key when only has to reach back to the oldest open transaction;
entries older than that can never conflict again and are dropped. A transaction that only reads
checks nothing and can never fail to commit.

Checking only the keys a transaction writes is exactly snapshot isolation: it stops lost updates,
because both transactions write the key they read, and it allows write skew, because there the
two transactions write different keys.

## Threads

- **The commit lock**: one commit, or one checkpoint, at a time. It is held across the flush,
  which is what makes commits one per flush. The record of written keys is only touched under it.
- **The cache lock**: the pages and their versions, the CLOCK hand, the last sequence number and
  the open snapshots. Evicting a changed page writes it while holding this lock.

The last sequence number and the open snapshots belong under the cache lock, and not under a
lock of their own, because of a race. A transaction begins by reading the last number and
registering it as open; a commit publishes by installing its page versions, keeping the old ones
if an older snapshot is open, and then advancing the number. If a commit could publish between
a transaction reading the number and registering it, the commit would not see the transaction,
drop the old versions it needs, and hand it pages from its future. Under one lock, beginning and
publishing cannot interleave.

Readers never take the commit lock, and the commit lock is always taken before the cache lock,
never the other way round. Milestone 1's rule that a thread holding a transaction cannot begin
another goes away: nothing waits for another transaction any more. Closing the database while
transactions are still open is an error.

## Crash safety

Nothing changes. The log still holds whole images of the pages each commit changed, recovery
still gives the last committed state, and nothing about snapshots or versions is ever on disk.
The crash tests of milestone 1 run again, unchanged, and must still pass.

## How milestone 2 is checked

- **One test per anomaly** in the table, each an exact interleaving written out step by step:
  the ones snapshot isolation prevents must not happen, and write skew and the read-only anomaly
  must.
- **A model of snapshot isolation.** Random interleavings of several transactions - begin, read,
  scan, write, commit and abandon, in a random order - run against the database and against a
  small reference implementation that keeps every committed version of every key. Every read,
  every scan and every commit's outcome must agree.
- **Bank transfers on real threads.** Threads move money between accounts, retrying when a commit
  conflicts, while other threads add up every balance. Every sum must equal the starting total,
  and so must the final one.
- **No version outlives its readers**: once every transaction has ended, the cache holds only the
  newest version of each page.
- **The crash tests** of milestone 1, unchanged.
- **The same concurrency benchmark** as before, for the difference ([results](../results/concurrency.md)).

## Order of work

1. Commit sequence numbers and page versions in the cache, made safe to share between threads;
   the registry of open snapshots; read transactions on snapshots.
2. Write sets: reads through them, and scans that merge them with the snapshot.
3. Commits that check for conflicts; the record of written keys, and dropping it.
4. The anomaly tests, the model of snapshot isolation, the bank transfers; the crash tests again.
5. The benchmark and the README.

## Known limits

- **Snapshot isolation, not serializable.** Serializable snapshot isolation (Cahill, Röhm and
  Fekete, 2008) would detect write skew by also tracking what transactions read; it is a possible
  later step.
- **One commit at a time, one flush each.** Grouping several commits into one flush is the obvious
  next step now that transactions run together.
- **A write transaction's changes must fit in memory**, and a transaction left open keeps the old
  page versions it might read in memory until it ends.
