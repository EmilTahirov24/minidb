# Working in this repository

## Commands

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes    # check formatting
dotnet format                        # fix formatting
```

## Layout

- `src/MiniDb` - the database.
- `tests/MiniDb.Tests` - the tests, including the simulated-disk crash tests.
- `docs/plan.md` - milestones and dates. `docs/design/` - one design document per milestone,
  written before its code. `docs/decisions.md` - why things are the way they are; add to it
  when a choice was not obvious.

## Invariants

Breaking one of these is a bug, not a trade-off.

1. `Commit` returns only after the transaction's commit frame is flushed to disk.
2. No page reaches the data file before its image is flushed to the log.
3. A page whose checksum fails is never used as data.
4. Recovery is idempotent: running it again, after a crash in the middle of it, gives the same
   database.
5. After recovery the database equals its state as of the last commit that returned, or of a
   commit that was in progress. Never anything in between.
6. Numbers in the README come from a run of a test or benchmark, with the command that produced
   them. If a command cannot reproduce a number, it does not belong in the repository.

## Conventions

- English for code, comments, commits and documentation.
- Commit messages say what changed and why, in the imperative. One concern per commit.
- Tests name the behaviour they protect, not the method they call.
- A test that has never failed on a broken version of the code is not trusted until it has.
- Every random test takes a seed and prints it when it fails.
