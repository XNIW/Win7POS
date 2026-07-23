# Settings monotonic reservation concurrency hardening

## Goal

Eliminate the same-process SQLite writer convoy that can make concurrent
`ReserveMonotonicIntAsync` calls fail with `SQLITE_BUSY`, while preserving the
existing fail-closed monotonic reservation contract.

## Files

- `src/Win7POS.Data/Repositories/SettingsRepository.cs` — per-database async
  reservation barrier around the existing immediate SQLite transaction.
- `tests/Win7POS.Core.Tests/Data/SettingsRepositoryMonotonicIntTests.cs` —
  synchronized multi-wave concurrent reservations and barrier-release
  regression coverage.

## Invariants

- The barrier is shared by repository instances targeting the same normalized
  database path, but does not serialize independent database files.
- The barrier is acquired before opening the SQLite connection and released in
  an outer `finally`, including invalid/corrupt-state failures.
- `BEGIN IMMEDIATE` (`deferred: false`), read/validate/UPSERT/commit ordering,
  rollback behavior and fail-closed corrupt/exhausted-state semantics are
  unchanged.
- SQLite remains the authority for coordination with other processes; this
  change neither increases the global busy timeout nor retries arbitrary
  database failures.

## Risks and negative cases

- Several concurrent waves must reserve one contiguous sequence with no reuse
  and with the persisted final value equal to the highest reservation.
- A corrupt setting must fail without stranding the barrier, so a subsequent
  repaired reservation can complete.
- Invalid requested values retain their existing argument failures before
  touching SQLite.

## Benchmark

Not applicable as a throughput change: the protected region is one short,
existing transaction. Validation uses deterministic concurrent stress waves
and repeated focused test runs rather than a timing threshold.

## Acceptance evidence

- Three synchronized waves of 24 independent repository/factory instances
  reserve exactly `1..72` and persist `72` as the high-water mark.
- A corrupt-state failure releases the shared barrier; after repair, a fresh
  repository reserves the next value within the bounded completion guard.
- The focused test class passed repeatedly (20 consecutive stress invocations)
  and the full local Core test suite passed 574/574.
