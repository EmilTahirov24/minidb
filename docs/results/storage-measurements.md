Keys of 16 bytes, values of 100.

A commit of one new key, 1,000 of them, with no checkpoint in between:

| Engine | Log bytes per commit | Of which data |
|---|---|---|
| MiniDB | 4,775 | 2.4 % |
| SQLite | 6,641 | 1.7 % |

100,000 keys loaded 1,000 to a transaction, then a checkpoint; then a random half of them deleted, then a checkpoint:

| Engine | Keys went in | Data file | Data in it | After deleting half | Free pages then |
|---|---|---|---|---|---|
| MiniDB | increasing | 12.0 MiB | 92 % | 12.0 MiB | 0 |
| MiniDB | random | 17.1 MiB | 65 % | 17.1 MiB | 0 |
| SQLite | increasing | 13.5 MiB | 82 % | 13.5 MiB | 508 |
| SQLite | random | 13.4 MiB | 82 % | 13.4 MiB | 551 |
