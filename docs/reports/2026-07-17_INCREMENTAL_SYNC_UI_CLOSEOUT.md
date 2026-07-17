# Win7POS incremental sync and UI closeout — 2026-07-17

## Scope and provenance

- Feature branch: `feature/win7pos-incremental-sync-ui-v2-20260716-201301`.
- Audit parent: `b590335348937ca830c92289c84032523e267497`.
- Audited `origin/main`: `5160b7c1574313ac8be47fdf2e139bb715a37e7d`.
- Parent orchestrator: Codex `/root`; four read-only subprocess audits completed.
- `main` was neither checked out for mutation nor merged into the feature branch.

Implementation commits preceding this report:

1. `28f1df5` — `core: define incremental-first catalog sync policy`;
2. `ecb4123` — `fix: fence catalog and sales sync transitions`;
3. `031d135` — `feat: add adaptive incremental sync coordinator`;
4. `fe06b25` — `ui: add modern sync center and shared visual polish`;
5. `99d625f` — `test: cover incremental resume concurrency and UI states`.

The documentation commit cannot include its own final SHA in this file.

## Delivered behavior

- Pure Core policy selects incremental/resume for every normal trigger. Full is
  limited to `FirstBootstrap`, unrecoverable `MissingShopBinding`/
  `MissingLegacyCursor`, `CursorRejectedOrExpired`, `ServerRequestedReset`,
  `ShopChanged`, `RestoreRecovery`, `ExactnessRepair`, authorized
  `AdministratorRepair`, or an explicitly incompatible
  `MigrationInvalidatedCursor`.
- Full is explicitly forbidden for timeout, offline, auth denial, ordinary retry,
  stale age, periodic/start-of-day/foreground/network triggers, manual sync,
  import ACK and page-budget exhaustion.
- Catalog page apply, checkpoint, exactness and repair completion are fenced by
  shop, transition epoch, previous mode and previous cursor.
- A server-selected full response establishes a new generation and is re-requested
  before rows can apply. Late responses after restore/transition cannot commit.
- Sales preflight blocking is compare-and-swap on outbox/sale/status/attempt/lease
  evidence. Transport and application-level `Ok` guards precede ACK mutation.
- Caller cancellation remains cancellation; internal timeout diagnostics are
  generation-fenced.
- The adaptive coordinator is single-flight with dirty-bit coalescing, bounded
  follow-up, checkpoint resume, randomized idle cadence and bounded jittered
  backoff. Auth denial stops polling and no periodic path schedules full.
- The WPF Sync Center separates incremental synchronization from authorized full
  repair, restores scanner focus after incremental work and presents only safe
  diagnostics. Shared resources improve focus, spacing and responsive behavior on
  POS, Payment and Products without new UI dependencies.
- New copy is available in IT/EN/ES/ZH.

## Automated validation

| Evidence | Result |
| --- | --- |
| Restore | PASS |
| Canonical source gates | 29/29 PASS |
| Required focused sync/UI/dialog/economics gates | PASS |
| Solution Release build | PASS, 0 warnings, 0 errors |
| Core/Data suite | 204/204 PASS, 0 skipped, 13 s |
| CLI selftest | `自检 PASS`; isolated DB retained by the harness |
| WPF | `net48`, Release, x86, 0 warnings, 0 errors |
| Policy matrix | 34/34 PASS |
| Normal trigger soak | 100/100 incremental or resume; no full |
| Release pack completeness | PASS |
| Win7 runtime-pack validator | PASS; PE x86 and prerequisites present |
| Installer | PASS; Inno Setup 6.7.3 generated `Win7POS-Setup.exe` |

The four previously open sync audit findings are resolved in this branch:
`SYNC-01` preflight CAS, `SYNC-02` response `Ok` guard, `SYNC-03` caller
cancellation, and `SYNC-04` restore/epoch fencing.

## Reproducible synthetic performance

Measurements use fresh temporary SQLite databases and deterministic generated
catalog rows. They cover local apply/reconcile behavior, not HTTP, JSON parsing,
staging latency or authenticated UI responsiveness.

| Scenario | Samples (ms) | Median | Exactness / notes |
| --- | --- | ---: | --- |
| delta proxy, 10 rows | 47.944; 15.611; 17.383 | 17.383 ms | 10 products/prices, pending 0 |
| delta proxy, 100 rows | 61.251; 26.772; 25.999 | 26.772 ms | 100 products/prices, pending 0 |
| delta proxy, 1,000 rows | 188.585; 162.696; 157.356 | 162.696 ms | 1,000 products/prices, pending 0 |
| legacy, 2,000 rows | 13915.974; 13800.445; 13826.718 | 13826.718 ms | products/prices exact, pending 0 |
| batch, 2,000 rows | 244.492; 229.360; 205.705 | 229.360 ms | 60.28x legacy/batch ratio |
| full, 19,762 rows | 4070.185; 3558.768; 3865.609 | 3865.609 ms | `Verified` 3/3, pending 0 |

The full median is 22.67% faster than the audited branch baseline of 4999.051 ms.
Maximum observed synthetic benchmark working set was 184,729,600 bytes versus the
182,624,256-byte audited baseline (+1.15%, below the +20% guardrail). This harness
runs under the x64 .NET test host and is not mislabeled as an x86 peak.

The actual x86/net48 WPF process was also observed at the isolated first-login
screen: peak working set 197,566,464 bytes and private bytes 128,020,480. A real
catalog pull was not performed in that process, so this is first-login evidence,
not an x86 full-sync memory certification.

TRX evidence is generated under `tests/Win7POS.Core.Tests/TestResults/` and remains
ignored by Git.

## Visual evidence

An isolated `--safe-start` launch used a fresh data root and no stored production
session. On the available host configuration (1440×900, 200% scale), the shell and
first-login dialog rendered without clipping; initial focus was on Shop code and
the credential fields were empty. The redacted screenshot was stored as external
evidence under the identifier `win7pos-first-login-current-host-redacted`; the
artifact and host path are not tracked in the repository.

The requested 1024×768 and 1366×768 configurations at 100%/125%, authenticated
POS/Payment/Products/Settings/Sync Center states and start-of-day flow were not
executed: changing host display settings was out of bounds and no test credentials
were available. Static UI/dialog guards passed, but they do not replace that
runtime matrix.

## Release result and remaining external evidence

The local x86 release pack and installer were generated and passed their validators.
They remain ignored build artifacts and are not part of the feature commit.

Not claimed as PASS:

- authenticated Admin Web/staging catalog and sales flows;
- authoritative staging comparison to 19,762 products;
- physical or VM Windows 7 SP1 startup/install/sale/reopen;
- Xprinter, scanner and cash-drawer behavior;
- the six requested display/DPI configurations and authenticated visual flows;
- x86 full-sync peak/private-set measurement.

These are the only remaining blockers to the corresponding external/runtime
certifications; they do not invalidate the completed local software gates.
