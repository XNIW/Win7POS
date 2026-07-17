# Win7POS final closure and merge review — 2026-07-17

## Decision

- Classification: `STOPPED_STAGING`.
- Task status: `NOT_DONE`.
- Merge recommendation: `NOT_READY_FOR_MERGE`.
- Main changed: `NO`.
- PR/push/merge: not performed because mandatory authenticated staging, physical
  Windows 7, dual-monitor, peripheral and DPI evidence is absent.

## Git provenance

- Initial `origin/main`: `5160b7c1574313ac8be47fdf2e139bb715a37e7d`.
- Source branch: `codex/win7pos-sync-drain-closeout-20260717`.
- Source HEAD: `75be03853a95cbb1b38db249b2b332f3f3549a32`.
- Review branch: `release/win7pos-final-review-20260717-015659`.
- Runtime-fix commit: `fe03719be0657cea882948f275674033d64fbdeb`.
- Final documentation commit: the commit containing this report.
- Reviewed interval: 22 commits, 109 changed files, divergence from initial main
  `0 22` before the review fix.
- Required ancestors `5160b7c`, `b590335`, `74780e9`, `32dec1e`, `6c321d6` and
  `75be038` all passed `merge-base --is-ancestor` against the source HEAD.
- Missing branch work: none demonstrated by the read-only provenance review.
- Backup preserved: `backup/pre-final-closure-20260717-015659`.
- Bundle preserved and verified: `Win7POS-pre-final-closure-20260717-015659.bundle`.

## Complete review findings

Five read-only review lanes completed across Git/provenance, catalog/performance,
sales/outbox/reversals, UI/hardware, and architecture/release/privacy. Findings
without reproducible evidence were rejected or retained as non-blocking risks.

| Severity | Found | Fixed | Open/deferred | Result |
| --- | ---: | ---: | ---: | --- |
| P0 | 0 | 0 | 0 | none |
| P1 | 3 | 3 | 0 | endpoint-offline scheduler recovery; customer display optional-init isolation; adaptive settings sizing |
| P2 | 11 | 2 | 9 | personal artifact path and periodic-full wording fixed; integration depth, long full/checkpoint, performance, cursor transactionality, identify-window/language lifecycle and CI hardening deferred |

The P1 patches do not alter outbox payload/hash/idempotency, refund/void economics,
WAL/schema policy or full-refresh reasons. Authentication denial still stops
polling; only retryable endpoint-offline failures keep bounded polling alive.

## Local validation

| Check | Result |
| --- | --- |
| Restore | PASS, .NET SDK 10.0.301 |
| Canonical gates | PASS, 30/30 |
| Individual required gates | PASS, 12/12 |
| Solution Release build | PASS, 0 warnings / 0 errors |
| Core/Data tests | PASS, 249/249, zero skipped |
| CLI selftest | PASS, including refund/void |
| WPF | PASS, net48 Release x86, 0 warnings / 0 errors |
| UiSmokeHarness | PASS build, net48 Release x86 |
| Secret/artifact review | PASS for tracked diff and local release pack |

Catalog policy tests cover healthy periodic incremental selection, evidence-driven
full reasons, idle polling at 24–36 seconds, fast catch-up, bounded retry, auth stop,
endpoint-offline recovery without a NIC transition, coalescing and backoff reset.
Healthy periodic polling never requests full; persisted repair evidence remains an
independent full reason.

## Performance and exactness

The 2,000-row controlled run completed three legacy and three batch samples. Median
legacy was 19,951.492 ms; median batch was 248.930 ms; ratio `80.15x`, pending 0.

The synthetic full-path 19,762-row run completed three 1,000-row-paged samples:
4,293.185 ms, 4,139.428 ms and 4,583.454 ms. Median was 4,293.185 ms, peak observed
working set 176,037,888 bytes, exactness `Verified` 3/3 and pending prices 0. This
is deterministic local evidence, not an authenticated staging full bootstrap.

## Release and installer

The clean-tree x86 release build, release-pack completeness validator, Win7 runtime
validator and Inno Setup compilation passed. The pack contains PE32/x86 WPF net48
and excludes UiSmokeHarness, CLI diagnostics, fixtures, screenshots, DB/WAL/SHM,
logs, PDB, source, QA credentials and secret markers. `Win7POS-Setup.exe` was
created locally. Installer smoke on Windows 7 is `NOT_RUN`.

## Mandatory staging evidence

No QA credential was supplied through an authorized interactive login flow, so no
authenticated staging mutation was attempted. The following remain `NOT_RUN`:

- first bootstrap full, pages/rows/duration and x86 process memory;
- product create/update/price/stock/category/supplier/tombstone delta arrival;
- cursor advance, partial resume, network recovery and unchanged full count;
- cash/card/mixed/offline sales, ACK/idempotency, refund and void server checks;
- application-layer 60-sale fixture (`60 → 35 → 10 → 0`) and remote duplicate audit;
- local/server daily reconciliation, midnight, timezone and business-date cases.

Local deterministic policy evidence confirms the 25-row bound and sequence `60 →
35 → 10 → 0`, but it cannot be promoted to staging PASS.

## Windows 7, visual and hardware evidence

The available machine is Windows 11 with .NET Framework 4.8, one 1440×900 monitor,
Microsoft virtual printers only, and no scanner, Xprinter or cash drawer. It cannot
certify a physical Windows 7 SP1 POS or Windows Extend.

- Interactive XAML surfaces discovered: 43 (3 windows, 34 dialogs, 6 controls).
- Prior real-entrypoint visual PASS remains 2 surfaces; prior harness-only coverage
  remains 6 types. This run added no runtime visual PASS.
- 1024×768 and 1366×768 at 100%/125%, 1024×600 best effort, IT/EN/ES/ZH runtime,
  physical dual-monitor focus, customer display, hot-plug and DPI remain `NOT_RUN`.
- Scanner, Xprinter, cash drawer and installer smoke remain `NOT_RUN`.
- Static/lifecycle policy tests and build evidence are not labeled as physical PASS.

Required continuation inputs are: authorized non-production QA shop/staff credential
entered interactively; a physical Windows 7 SP1 x86 POS with .NET 4.8; two independent
monitors in Extend mode; scanner; Xprinter; cash drawer; and authorization to create
prefixed staging QA products/sales through application services.

## Task ledger and publication

No exact pre-existing task-ledger entry was found for the full closure definition,
so this report and `docs/AI_WORKLOG.md` record `NOT_DONE`; no issue ID was invented.
Because the Definition of Done is incomplete, the review branch was not pushed, no
PR was created, no CI was requested, and local/GitHub main were not moved.

## Separate non-blocking backlog

Architecture PR 1–5 remain independent: repository/service decomposition, catalog
orchestration split, UI composition cleanup, broader integration fixtures and CI
supply-chain hardening. They are explicitly excluded from this merge attempt and
do not relax the external staging/hardware blockers above.
