# PR-A persistence foundation closeout — 2026-07-17

## Publication status

- Base: GitHub `main` at `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`.
- Implementation commit: `188d9cd3cb4c1728f802fc114088e3fd18ecc3c7`.
- Branch: `codex/pr-a-persistence-foundation-20260717-114614`.
- Pull request: GitHub `#5`, open and non-draft.
- Implementation CI: run `29595607766`, `completed/success` on `188d9cd`.
- Structural status: `READY_FOR_REVIEW`.
- Auto-merge: `NO`; this execution does not merge the PR.

## Scope delivered

PR-A adds a small persistence safety layer without a schema, WAL-policy, public
workflow, catalog protocol, outbox payload/hash or refund/void economics change:

- manual and pre-restore backups use SQLite `BackupDatabase` and validate both
  `integrity_check` and `foreign_key_check` before publication;
- a process-wide factory fence drains tracked connections, blocks new opens during
  the swap, supports maintenance-owner re-entry and always releases after failure;
- restore writes a durable same-directory prepared marker, copies the validated
  candidate durably and uses `File.Replace` to obtain an atomic rollback file;
- prepared/committed recovery is idempotent, validates live and rollback databases,
  removes journal/WAL/SHM sidecars under quiescence and never discards the rollback
  merely because a live file exists;
- post-commit cleanup is deferred safely if the filesystem refuses deletion; a
  later startup validates the live database before retrying cleanup;
- startup runs interrupted-restore recovery before normal DB initialization.

`SqliteConnectionFactory` is included because a workflow-local semaphore cannot
quiesce background sales/catalog/settings connections. The public factory methods
remain source compatible and no dependency was added.

## Files in the functional commit

- `src/Win7POS.Data/Backup/SqliteOnlineBackup.cs`
- `src/Win7POS.Data/Online/AtomicRestoreInstaller.cs`
- `src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs`
- `src/Win7POS.Data/SqliteConnectionFactory.cs`
- `src/Win7POS.Wpf/Pos/PosWorkflowService.cs`
- `tests/Win7POS.Core.Tests/Data/PersistenceFoundationTests.cs`
- `tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs`
- `tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs`
- `scripts/check-win7pos-restore-guard.ps1`

## Restore, concurrency and crash evidence

Focused tests cover online backup while a writer commits against a 32 MiB fixture,
zero writer failure, at least one overlapping successful write and a latency below
the existing five-second busy timeout. Other cases cover rejected FK violations
without changing the live hash, source TOCTOU, post-swap failure rollback, prepared
crash state before and after the atomic swap, committed crash state, corrupt
committed live recovery, rollback-journal removal, repeat recovery, DB reopen and
combined integrity/FK validation. The connection tests cover active drain, blocked
non-owner open, owner re-entry and recovery from a leaked owner handle.

Results:

| Check | Result |
| --- | --- |
| Focused persistence/restore/connection tests | `16/16 PASS` |
| Full Core/Data tests | `257/257 PASS`, skipped `0` |
| Canonical required gates | `30/30 PASS` |
| Solution Release | `PASS`, 0 warnings / 0 errors |
| CLI selftest | `PASS` |
| WPF net48/x86 | `PASS`, 0 warnings / 0 errors |
| Architecture boundaries | `PASS` |
| Restore structural guard | `PASS` |

All databases are disposable temporary fixtures. No real POS or production DB was
opened.

## Same-host A/B performance

The comparison used three fresh temporary databases per mode on both exact main
`f3e779b` and PR-A. Values are medians; working set is the maximum observed sample
completion value.

| Scenario | Main before | PR-A after | Change | Other evidence |
| --- | ---: | ---: | ---: | --- |
| Legacy 2,000 | 15,958.884 ms | 16,543.280 ms | +3.66% | pending 0 |
| Batch 2,000 | 405.183 ms | 425.076 ms | +4.91% | pending 0; batch/legacy `38.92x` |
| Paged full 19,762 | 4,412.304 ms | 4,311.045 ms | -2.29% | `Verified` 3/3; pending 0 |
| Peak working set, full | 159,842,304 B | 159,387,648 B | -0.28% | no anomalous x86 growth observed |

The required ratio is at least `5x`; observed PR-A ratio is `38.92x`. No median
regression exceeds 20%. PR-A does not alter catalog transactions, statement shape,
context loading, HTTP buffering or commit count.

## Release and risk decision

The clean-tree x86 Release Pack, completeness check, Win7 runtime validator and
local installer build are required and executed on the documented branch head.
The pack must exclude harnesses, fixtures, DB/journal/WAL/SHM files, logs, PDB,
screenshots, secrets and QA configuration.

- P0 open: `0`.
- P1 open: `0` after the review fixes for journal cleanup, corrupt committed-live
  validation, post-commit cleanup outcome and leaked-owner fence release.
- Remaining historical P2: `9`, unchanged by this single-scope PR.
- Residual PR-A external risk: real power-loss/filesystem behavior on Win7/x86 is
  not certified without a qualifying Windows 7 target; this is not labeled PASS.

Rollback is a normal revert of PR-A: there is no schema migration, persisted
business payload version or WAL policy to downgrade. Production certification
remains `OPEN` because external certification is still `0/16`.
