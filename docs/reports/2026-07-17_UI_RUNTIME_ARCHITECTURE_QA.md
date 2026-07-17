# Win7POS UI runtime and architecture QA — 2026-07-17

## Result

Classification: **STOPPED_RUNTIME_BLOCKED**.

The QA branch is based on feature commit
`74780e98d74a53bcbc449eeed3558049026a5480`; `main` was not modified or merged.
The public staging server was reachable from the real Win7POS first-login flow,
but the session had no valid authorized staging account. Consequently, no POS,
catalog, sales, refund, settings or Sync Center state that requires a trusted
session is claimed as a staging visual PASS.

## Runtime UI evidence

- Dynamic inventory: 39 interactive surfaces (one Window, 32 dialogs, six views).
- Real public-staging entrypoint opened with Computer Use: MainWindow and
  PosOnlineFirstLoginDialog.
- QA harness opened with real WPF XAML: Settings Hub, Sync Center, start-of-day,
  Daily Report dialog, User Management dialog and Product Edit dialog.
- Computer Use visual PASS: MainWindow, first-login, Daily Report dialog and User
  Management dialog on the current host profile.
- Current host: 2880×1800 physical / 1440×900 logical, 200%, one monitor, EN.
- Other required DPI profiles and IT/ES/ZH critical matrices: NOT_RUN.
- Win7 physical/VM, multi-monitor, Xprinter, scanner and cash drawer: NOT_RUN.

The detailed inventory and honest PASS/BLOCKED/NOT_RUN matrix are in
`docs/QA/WIN7POS_UI_RUNTIME_MATRIX.md`. Screenshots, environment metadata and CSV
evidence remain outside Git under `C:\Dev\Win7POS-QA\20260716-215939\visual`.

## Runtime-observed fixes

### Maximize-only workstation shell

The reported shrink/restore behavior was reproduced as a window-state risk. The
main shell now starts maximized, uses `ResizeMode="CanMinimize"`, and rejects a
transition to `WindowState.Normal`. Computer Use confirmed the real first frame
uses the full work area, exposes Minimize and Close, and reports Restore disabled.
A QA WPF probe confirmed `Normal → Maximized`, `Minimized → Minimized`, and restore
`Normal → Maximized`. No Loaded sizing, custom Left/Top or dialog behavior changed.

### Daily Report lifecycle retention

The first 20× lifecycle run retained all 20 DailyReportViewModel instances through
the static localization event. DailyReportViewModel now implements idempotent
`IDisposable`; DailyReportDialog unsubscribes and clears its DataContext on Closed.
The repeated run passes with zero non-harness windows, language-handler count 0→0,
and no strictly monotonic handle or private-byte sequence. Weak references are kept
as diagnostics only because WPF/async continuations may retain the last instance.

P0: 0 found, 0 fixed, 0 open. P1: 1 found, 1 fixed, 0 open. P2: 0 found,
0 fixed, 0 deferred. The lifecycle retention is tracked separately as an
architecture/runtime defect and is fixed.

## Architecture and test completion

- `check-architecture-boundaries.ps1` now parses `Win7POS.slnx`, enumerates every
  source/test csproj, fails unknown projects, verifies reference and target shapes,
  and classifies 7/7 current projects.
- CatalogSyncPolicy remains pure Core; CatalogSyncCoordinator remains UI-agnostic
  Data; Sync Center has no direct SQL or HTTP transport.
- Core has no WPF/Data/SQLite/HTTP/Excel implementation dependency; Data has no
  WPF dependency; WPF has no direct SQLite/Dapper usage; no direct Supabase/secret
  markers were found.
- Persisted sync payload gates continue to redact token, PIN, password, credential
  and full workbook paths.
- Normal WPF/sync runtime paths contain no `Wait()` or
  `GetAwaiter().GetResult()`. The explicit supplier Excel QA smoke dispatcher pump
  remains an identified test-only exclusion, not a normal runtime path.
- The release completeness gate now rejects UiSmokeHarness binaries, QA fixtures,
  matrix files, lifecycle/window-state results and screenshots.
- A SQLite trigger failure-injection test now proves sale header, lines, stock
  movement, stock decrement and sales outbox roll back together when the final
  outbox insert aborts.

Startup architecture remains **PARTIAL**: MainWindow still owns online startup
workflow, and the full new/legacy/unavailable DB plus trusted/untrusted/revoked
matrix is not extracted into deterministic headless tests. This report therefore
does not claim architecture complete.

## Final automated and release validation

| Evidence | Result |
| --- | --- |
| Restore | PASS |
| Canonical source gates | 29/29 PASS |
| Architecture, dialog and focused sync/UI gates | PASS |
| Solution Release build | PASS, 0 warnings, 0 errors |
| Core/Data tests | 205/205 PASS, 0 skipped |
| Financial outbox failure injection | PASS |
| CLI selftest | PASS on isolated temporary DB |
| WPF and UiSmokeHarness | net48/x86 PASS, 0 warnings, 0 errors |
| MainWindow maximize-only WPF probe | PASS |
| Six-surface lifecycle, 20 cycles each | PASS |
| Release pack completeness | PASS |
| Win7 runtime release validator | PASS; WPF PE x86 |
| UiSmokeHarness/QA artifacts excluded | PASS |
| Installer | PASS; `Win7POS-Setup.exe` generated |

## Decomposition status

- PR 1 startup coordinator: PARTIAL.
- PR 2 catalog state machine: PARTIAL.
- PR 3 ProductRepository split: NOT_STARTED.
- PR 4 SaleRepository split: NOT_STARTED (failure-injection prerequisite added).
- PR 5 schema migrations: NOT_STARTED.

No large PR 1–5 refactor was performed.

## Remaining blockers

1. Authorized, non-production staging credentials are required for the trusted
   session and authenticated UI/state matrix.
2. True Windows DPI changes for 1024×768 and 1366×768 at 100%/125% must be run and
   restored; resizing a window is not accepted as a substitute.
3. Windows 7 SP1 and attached printer/scanner/cash-drawer hardware require their
   respective machine and devices.
4. Multi-monitor owner/work-area validation requires a second monitor.

Merge recommendation: **NOT_READY_FOR_MERGE** until the authenticated staging and
required display/DPI matrix are completed.
