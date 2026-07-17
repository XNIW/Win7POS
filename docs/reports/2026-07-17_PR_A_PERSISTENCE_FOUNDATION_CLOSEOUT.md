# PR-A persistence foundation closeout — 2026-07-17

## Publication status

- Base: GitHub `main` at `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`.
- Implementation commit: `188d9cd3cb4c1728f802fc114088e3fd18ecc3c7`.
- Final-review blocker fix: `607e1f15fce64fed48e84e5fe680d40741ef6031`.
- Branch: `codex/pr-a-persistence-foundation-20260717-114614`.
- Pull request: GitHub `#5`, merged and non-draft.
- Final PR CI: run `29600241291`, `completed/success` on exact head `607e1f1`.
- Structural status: `DONE_MERGED`.
- Merge method: fast-forward `f3e779b..607e1f1`; squash/rebase/force push `NO`.
- GitHub author self-approval was rejected by platform policy; a formal review
  comment records the equivalent evidence before the authorized fast-forward.

## Scope delivered

PR-A adds a small persistence safety layer without a schema, WAL-policy, public
workflow, catalog protocol, outbox payload/hash or refund/void economics change:

- manual and pre-restore backups use SQLite `BackupDatabase` and validate both
  `integrity_check` and `foreign_key_check` before publication;
- a process-wide factory fence drains tracked connections, permits completion opens
  only until the first zero-connection boundary, then blocks non-owner opens during
  maintenance, supports owner re-entry and aborts before the action after a bounded
  30-second drain timeout;
- after that drain, restore revalidates the trusted shop, catalog epoch, both live
  outboxes and the already-validated candidate before pre-backup or live swap;
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
combined integrity/FK validation. The connection tests cover active drain, an open
needed to complete the drain, the first-zero admission boundary, blocked non-owner
open while the owner has a connection, bounded drain timeout, owner re-entry and
recovery from a leaked owner handle. A two-factory regression commits an outbox row
after preliminary validation and proves the fenced guard aborts before the swap.

Results:

| Check | Result |
| --- | --- |
| Focused persistence/restore/connection tests | `19/19 PASS` |
| Full Core/Data tests | `260/260 PASS`, skipped `0` |
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

- P0 open: `0` after closing the pre-fence restore/outbox TOCTOU found in final
  review.
- P1 open: `0` after the earlier journal/recovery fixes plus the final-review
  nested-open drain deadlock, first-zero admission and bounded-timeout fixes.
- Remaining historical P2: `9`, unchanged by this single-scope PR.
- Residual PR-A external risk: real power-loss/filesystem behavior on Win7/x86 is
  not certified without a qualifying Windows 7 target; this is not labeled PASS.

Rollback is a normal revert of PR-A: there is no schema migration, persisted
business payload version or WAL policy to downgrade. Production certification
remains `OPEN` because external certification is still `0/16`.

Post-merge evidence on exact software head `607e1f1`:

- main CI `29600645459`: `completed/success`, 30/30 gates, 260/260 tests,
  CLI selftest and WPF net48/x86;
- Release Pack `29600645440`: `completed/success`, 30/30 source gates, 33/33
  packaging gates, WPF x86 and Inno Setup installer;
- downloaded pack `VERSION.txt`: exact `CommitSHA=607e1f1...`, `Ref=main`,
  `TreeState=clean`;
- GitHub installer SHA-256:
  `0F54924EE6B5C15D96626885E6D1A3D59D3A85EE7CC65961D0B49572C7C748D6`;
  release ZIP SHA-256:
  `E35188BCE9E32C38FFE9290E1635E16F51A6710BCB3ED339D5FB63B071194260`.
