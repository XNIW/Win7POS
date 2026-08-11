# SQLITE-DURABILITY-001 mini-spec

## Problem

`SqliteConnectionFactory` previously relied on SQLite/provider defaults for its
journal and synchronous modes.  The observed values happened to be compatible,
but the application did not select or verify a durability policy when a
connection was opened.

## Scope and invariants

- Keep the rollback-journal (`DELETE`) model.  It is the conservative choice for
  the Windows 7 support and backup/restore model because it does not introduce
  `-wal` / `-shm` sidecars that must be copied, restored, and validated as a set.
- Require `synchronous=FULL` for committed writes.  SQLite's `FULL` setting asks
  the VFS to synchronize the rollback journal at the commit boundary; actual
  power-loss guarantees remain bounded by the filesystem, storage controller,
  and their flush semantics.
- Enable and verify `foreign_keys=ON` and `busy_timeout=5000` on every physical
  connection, because both settings are connection-local.
- Set `temp_store=FILE` to avoid intentionally retaining large temporary
  operations in the x86 working set, and set `cache_size=-2048` (a 2 MiB
  per-connection page-cache target) to make that cache budget explicit.  The
  negative cache value is KiB, independent of the database page size; it is not
  a claim about total process working-set size.
- Select and then read back every pragma.  If SQLite cannot apply or report the
  exact policy, opening the connection fails before repositories can publish
  state.
- This item does not change application transaction ownership, source-of-truth
  ledger states, migrations, backup format, or restore protocol.

## Acceptance criteria

1. New, existing, and reopened databases report exactly `delete`, `FULL (2)`,
   foreign keys enabled, a 5000 ms busy timeout, `FILE (1)` temporary storage,
   and a -2048 KiB cache budget.
2. A transaction that fails or is rolled back leaves no durable partial row; a
   fresh pooled/reopened connection keeps the same verified policy and passes
   integrity and foreign-key checks.
3. A concurrent writer observes the configured busy timeout and can write after
   the owner rolls back.
4. Tests print only non-sensitive policy/size evidence (SQLite version, page
   size, page count, cache target, and database file bytes) to make the
   configured-memory choice reproducible.

## Files and risks

- `src/Win7POS.Data/SqliteConnectionFactory.cs` selects and verifies the policy.
- `tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs` exercises
  creation, reconnect, rollback/recovery and contention.

The main regression risk is failure to change `journal_mode` while another
connection owns a lock.  Failing the open is intentional: it prevents a caller
from continuing under an unverified durability policy.
