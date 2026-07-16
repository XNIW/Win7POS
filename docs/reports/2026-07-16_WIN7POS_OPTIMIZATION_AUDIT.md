# Win7POS optimization audit — 2026-07-16

## Executive result

The validated reconciliation commit `5160b7c1574313ac8be47fdf2e139bb715a37e7d`
was fast-forwarded to `main`, pushed without force, and verified as the common local
and remote head. Both required provenance commits are ancestors of the published
head:

- ASUS: `9411c8bc86a48284bfbc17897ce243bc3be90b2c`
- Mac: `dc162aeff484b576ef21565338cf3d5d492285d4`

All audit changes remain isolated on
`audit/win7pos-structure-sync-performance-20260716-193139`; no audit patch was
merged into `main`.

## Scope and method

One parent orchestrator performed all mutations. Five bounded read-only agents
completed independent reviews of architecture, SQLite/recovery, catalog and sales
sync, performance, and CI/release/Win7 compatibility. No child agent modified the
worktree, committed, or spawned another agent.

The parent retained only findings with file-level evidence, merged the overlapping
performance-harness findings `PERF-04` and `E-BUILD-001`, and rejected 21 hypotheses
that were disproved or lacked sufficient evidence.

| Severity | Deduplicated findings |
| --- | ---: |
| P0 | 0 |
| P1 | 23 |
| P2 | 9 |

## Result classes

| Class | Result |
| --- | --- |
| `PASS_AUTOMATED` | Restore, 29 source gates, solution build, 182 Core/Data tests, CLI selftest, WPF net48/x86, release-pack checks, and installer build passed. |
| `PASS_SYNTHETIC_PERFORMANCE` | 2,000-row legacy/batch comparison, 19,762-row full reconciliation, SQLite policy probes, and 20k/100k query-plan probes passed. |
| `BLOCKED_EXTERNAL_STAGING` | No authenticated staging endpoint or credentialed end-to-end test was in scope. |
| `NOT_RUN_WIN7_PHYSICAL` | No Windows 7 SP1 machine or VM was connected. Static/runtime-pack compatibility checks are not a physical smoke test. |
| `NOT_RUN_HARDWARE` | Xprinter, scanner, and cash drawer were not connected. |

## Final automated evidence

| Check | Result | Artifact or essential statistic |
| --- | --- | --- |
| Canonical source gates | PASS | `29/29` |
| Canonical release gates | PASS | `32/32` with `dist/Win7POS` |
| Solution build | PASS | Release, 0 warnings, 0 errors |
| Core/Data tests | PASS | `182/182`; `tests/Win7POS.Core.Tests/TestResults/final.trx` |
| CLI selftest | PASS | kept DB: `selftest_82c958c30a6e45d194d1de71c5087018.db` |
| WPF | PASS | net48, x86, 0 warnings, 0 errors |
| Release pack | PASS | `dist/Win7POS`; completeness and Win7 runtime validators passed |
| Installer | PASS | `installer/output/Win7POS-Setup.exe`, Inno Setup 6.7.3 |

## Deduplicated findings and disposition

### Architecture

| ID | Sev. | Evidence | Disposition |
| --- | --- | --- | --- |
| ARCH-001 | P1 | `MainWindow.xaml.cs:158,396,476,618` owns DB startup, trust/session, recovery, heartbeat and three sync workflows. | PLAN: extract a UI-neutral startup/online coordinator incrementally. |
| ARCH-002 | P1 | `PosCatalogPullService.cs:163,323,714,847,974` combines transport, paging, exactness, repair and sale-safe commit. | PLAN: façade plus page runner, pure chain validator, mapper and state writer. |
| ARCH-003 | P2 | `ProductRepository.cs:67,441,489,1642,2495` combines query, local write, remote identity, replay and price history. | PLAN PR 3; preserve the public façade and transaction boundaries. |
| ARCH-004 | P1 | `SaleRepository.cs:23,69,259,546,620,935` combines sale, reporting, reversal, stock and outbox state machine. | PLAN PR 4; keep sale+stock+outbox atomic. |
| ARCH-005 | P2 | `CatalogShopStateRepository.cs:83,671,701,738` mixes persistence representation with sale-safety policy. | PLAN: pure immutable snapshot/policy behind existing adapters. |
| ARCH-006 | P2 | `check-architecture-boundaries.ps1:56-65` uses a hard-coded project map without comparing it to `Win7POS.slnx`. | PLAN: make unknown solution projects fail the checker. |

### SQLite, backup, restore and migrations

| ID | Sev. | Evidence | Disposition |
| --- | --- | --- | --- |
| SQLITE-RESTORE-001 | P1 | `AtomicRestoreInstaller.cs:31-36,44-47` copies over the live DB instead of using a same-directory atomic replacement. | PLAN: atomic replace plus crash/reopen tests; not a low-risk isolated patch. |
| SQLITE-RESTORE-002 | P1 | The restored DB can become live before catalog quarantine/reset state is durable. | PLAN: pre-swap marker/fencing protocol. |
| SQLITE-BACKUP-001 | P1 | `PosWorkflowService.cs:615,645` checkpoints and then copies the raw DB while other repository instances can still write. | PLAN: SQLite online backup API and concurrent-write tests. |
| SQLITE-MIGRATION-001 | P1 | `DbInitializer` creates strict new tables but legacy `ALTER TABLE` paths do not rebuild all constraints. | PLAN PR 5: versioned migrations and upgrade fixtures. |
| SQLITE-INTEGRITY-001 | P1 | Candidate/post-swap validation uses `integrity_check` but does not establish `foreign_key_check`. | PLAN with restore fixture coverage. |
| SQLITE-DURABILITY-001 | P2 | `SqliteConnectionFactory.cs:15-44` centralizes FK and timeout but does not select/log journal mode or explicitly set synchronous. | DEFERRED: WAL changes backup/restore sidecars and needs a Windows/Win7 matrix. Current synthetic observation is `delete`, `FULL(2)`. |

### Catalog and sales synchronization

| ID | Sev. | Evidence | Disposition |
| --- | --- | --- | --- |
| SYNC-01 | P1 | `PosSalesSyncService.cs:104-106,442-452`; `SaleRepository.cs:1083-1099`: preflight blocking lacks an attempt-count CAS and can overwrite another worker's `in_progress` lease. | PLAN/SEPARATE FIX: read-only first-batch files; add a concurrent regression test before mutation. |
| SYNC-02 | P1 | `PosCatalogPullService.cs:340-413,714-750` does not prove `PosCatalogPullResponse.Ok` before apply/cursor progress. | PLAN/SEPARATE FIX: guard and protocol test; confidence MEDIUM. |
| SYNC-03 | P2 | `PosCatalogPullService.cs:1001-1039` converts caller cancellation into timeout/failure state. | PLAN: rethrow caller-requested cancellation and test diagnostics. |
| SYNC-04 | P1 | `CatalogShopStateRepository.cs:386-454,1177-1199`: restore-review reset lacks the transition barrier/epoch fencing used by other transitions. | PLAN: race test and consistent lock order before change. |
| SYNC-05 | P2 | Empty catalog version can persist multi-run delta checkpoints without authoritative snapshot identity. | PLAN: fail closed only after compatibility requirements are agreed. |

### Performance

| ID | Sev. | Evidence | Disposition |
| --- | --- | --- | --- |
| PERF-01 | P1 | `RemoteCatalogBatchRepository.cs:103-151` and `ProductRepository.cs:2043-2069` reload whole-catalog maps for every page: `O(pages × catalog)`. | PLAN: page-size/statement-count matrix, then scoped context or temp-table lookup. |
| PERF-02 | P2 | `ProductRepository.cs:243-348` performs count plus page; contains search and deep OFFSET scan. | MEASURED/DEFERRED: candidate category/supplier indexes help synthetic filters, but import/update regression is not yet measured. No index shipped. |
| PERF-03 | P1 | `PosAdminWebClient.cs:289-326` holds MemoryStream, copied bytes, UTF-16 string and re-encoded UTF-8 near the 8 MiB cap. | PLAN/SEPARATE FIX: single seekable buffer plus x86 allocation test. |
| PERF-04 / E-BUILD-001 | P1 | Performance project was outside the solution; opt-in tests previously accepted no samples and had no ratio assertion. | PARTIAL FIX: project in solution, scheduled/manual lane, TRX/text artifacts, non-empty samples and 5x median requirement. x86/net48 peak harness remains open. |
| PERF-05 | P2 | Price apply/repair repeatedly resolves owner, product, history and pending state per price. | PLAN: statement/allocation counters before any set-based rewrite. |

### CI, release and reproducibility

| ID | Sev. | Evidence | Disposition |
| --- | --- | --- | --- |
| E-CI-001 | P1 | CI lacked push-main; release also ran on PR and lacked tag trigger. | FIXED: CI PR/push-main, release push-main/`v*`/manual, no release PR. |
| E-GATES-001 | P1 | Canonical runner omitted existing sync, restore, startup and UI checkers. | FIXED: 29 source gates, explicit missing-file failure, 3 conditional release gates. |
| E-GATES-002 | P1 | `check-win7pos-legacy-db-migrations.ps1:103-112` performs implicit restore/build inside the gate. | OPEN: keep out of pre-build canonical list until static/runtime phases are split. |
| E-CI-002 | P1 | CI generated a TRX without retaining it. | FIXED: `always()` artifact upload. |
| E-CI-003 | P2 | No cancellation for superseded PR runs. | FIXED: PR/ref concurrency; releases are not cancelled. |
| E-RESTORE-001 | P1 | No `packages.lock.json` or locked restore mode. | PLAN: introduce locks in a dependency-only PR with native SQLite diff review. |
| E-REL-001 | P1 | CI can install the latest Inno compiler and installer version remains a separate fixed value. | PLAN: pin compiler and unify tag/product version. |
| E-REL-002 | P1 | Local `-BuildInstaller` can warn instead of fail when ISCC is absent. | PLAN: make requested installer/output absence fatal. This run did produce the installer. |
| E-WIN7-001 | P1 | Release runs on `windows-latest`; current validators are static/PE checks, not a Win7 smoke. | BLOCKED: require VM/physical Win7 evidence for a tagged release. |
| E-REL-003 | P1 | No Authenticode signing/timestamp verification. | PLAN/EXTERNAL: design SHA-2-compatible signing for Win7 before adding secrets. |

## Applied changes

- Completed the canonical gate manifest with explicit source/release phases and a
  machine-readable passed/total summary.
- Corrected CI and release triggers, added PR-only cancellation and retained TRX
  artifacts on failures.
- Added the performance harness to the solution and created a Windows,
  SDK-10.0.301, manual/scheduled benchmark lane with no secrets.
- Made benchmark requests fail on absent/incomplete samples and enforce a median
  batch/legacy ratio of at least 5x.
- Added temporary-DB safeguards for new/legacy SQLite initialization, two
  connections, 5-second busy timeout, rollback, checkpoint, reopen and integrity.
- Added 20k/100k production-shape query-plan probes before/after candidate indexes.

No production sync, economic, restore, repository, dialog, journal-mode or index
behavior changed in this batch.

## Rejected or unpromoted hypotheses

Twenty-one hypotheses were rejected or left unpromoted. The important cases were:

- no dependency cycle or Data-to-WPF inversion was found;
- `allowLegacyUnbound` and exactness `Unverified` have documented compatibility
  intent, so they are not defects without a changed requirement;
- the 15-minute lease takeover is protected by attempt-count CAS outside the
  separately identified preflight path;
- a 12-retry terminal policy was not contradicted by a business requirement;
- DataGrid virtualization, page size 200 and explicit search application disprove
  claims of full-catalog rendering or a DB query on every keystroke;
- the HTTP response is bounded at 8 MiB; the finding concerns duplicate
  allocations, not an absent limit;
- SDK 10.0.301, direct package versions, WPF net48/x86 and manual-only
  `wpf-build` were already correctly configured;
- missing gate files already failed explicitly; the issue was manifest coverage;
- no claim was made that a deployed database currently has FK violations,
  corrupt weak legacy constraints, or a specific live journal mode.

## Remaining blockers

1. Physical Windows 7 SP1 startup/sale/reopen/installer evidence.
2. Authenticated staging catalog and sales-sync evidence.
3. Xprinter, scanner and cash-drawer tests.
4. Atomic restore/online backup/fencing design and crash fixtures.
5. Attempt-count CAS and catalog-response guards with concurrent/protocol tests.
6. WAL/fallback/backup-sidecar matrix before any journal-mode change.
7. Locked NuGet graph, pinned Inno provenance and SHA-2-compatible signing.

Recommended independent PR order: PR 5 migrations, PR 1 startup coordinator,
PR 2 catalog state machine, PR 3 product repository internals, PR 4 sale repository
internals.
