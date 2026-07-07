# Codex Asus Task Index

This file tracks Asus Windows build-machine tasks using the same concise table-and-notes style used in `docs/reports/`. It is intentionally lightweight: detailed QA evidence still belongs in dated reports under `docs/reports/`.

## Task Template

```text
ASUS-W7POS-XXX
Title:
Status: Planned/In Progress/Done/Blocked
Base commit:
Files touched:
Goal:
Guardrails:
Commands:
Smoke:
Result:
Commit SHA:
Notes:
```

## Recent Tasks

### ASUS-W7POS-001

Title: Unified POS access login
Status: Done
Base commit: not explicitly tagged in git history
Files touched: `src/Win7POS.Wpf/MainWindow.xaml.cs`, `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml*`, login/session support files
Goal: Replace the old split initial login/bootstrap path with one POS access dialog that handles online-first access and offline fallback.
Guardrails: Keep WPF `net48`, x86, Windows 7 safe APIs, and no fallback to offline when Admin Web denies credentials.
Commands: Covered by `scripts/check-pos-unified-login-ux.ps1` and WPF x86 build in later tasks.
Smoke: Unified POS access startup smoke covered in subsequent Asus runs.
Result: Done.
Commit SHA: not explicitly tagged; current unified flow is present by `a4a9559`.
Notes: Current startup path uses `PosOnlineFirstLoginDialog` and the `POS access dialog opening/shown/accepted` markers.

### ASUS-W7POS-002

Title: Install .NET 10 and complete QA
Status: Done
Base commit: not explicitly tagged in git history
Files touched: QA reports and local build environment
Goal: Align the Asus build machine with SDK .NET 10 so `Win7POS.slnx`, CLI `net10.0` tests, and WPF `net48` x86 builds can run locally.
Guardrails: Do not change project targets; use `C:\Dev\dotnet10\dotnet.exe` when PATH SDK is older.
Commands: Restore, build, test, CLI selftest, WPF x86 build.
Smoke: Windows build-machine smoke only; physical Win7/hardware remains separate.
Result: Done.
Commit SHA: not explicitly tagged; related environment note exists in `docs/reports/2026-07-01_MAC_FINAL_ASUS_REVIEW_AND_MAIN_MERGE.md`.
Notes: Current required validation uses `C:\Dev\dotnet10\dotnet.exe`.

### ASUS-W7POS-003

Title: Wi-Fi badge and sync checklist UI polish
Status: Done
Base commit: not explicitly tagged in git history
Files touched: `src/Win7POS.Wpf/MainWindow.xaml*`, `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml*`, `src/Win7POS.Wpf/Infrastructure/NetworkStatusService.cs`
Goal: Improve network status presentation and POS access sync progress without changing login semantics.
Guardrails: No Win10+/WinUI/WebView2 dependencies; keep Win7-safe network detection.
Commands: `scripts/check-pos-unified-login-ux.ps1`, WPF x86 build.
Smoke: Header and POS access network badge visual smoke on Asus.
Result: Done.
Commit SHA: current UI pieces are present by `a4a9559`.
Notes: `NetworkStatusService` remains based on `System.Net.NetworkInformation`.

### ASUS-W7POS-004

Title: Compact POS access dialog layout
Status: Done
Base commit: `ae02efe`
Files touched: `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml*`, `src/Win7POS.Wpf/MainWindow.xaml*`, related unified-login checks
Goal: Tighten the first POS access screen and remove the empty visual gap while preserving the unified login.
Guardrails: No login logic changes, no target changes, keep dialog standard.
Commands: Dialog standards, unified login UX check, WPF x86 build.
Smoke: POS access visual smoke on Asus.
Result: Done.
Commit SHA: `a4a9559`
Notes: Commit also contains supporting unified-login and network-status adjustments.

### ASUS-W7POS-005

Title: Harden POS access login logging
Status: Done
Base commit: `a4a9559`
Files touched: `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs`, `src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs`, `src/Win7POS.Wpf/Infrastructure/FileLogger.cs`, `src/Win7POS.Wpf/MainWindow.xaml.cs`, `scripts/check-pos-login-logging.ps1`
Goal: Add safe, stage-based POS access logging with attempt ids, results, duration, and secret redaction.
Guardrails: Never log PIN/password/credential/token values; do not change UI or login flow.
Commands: `scripts/check-pos-login-logging.ps1`, full restore/build/test/selftest/WPF x86 validation.
Smoke: `C:\POSData\LoginLoggingSmoke` with empty fields and unreachable Admin Web fallback.
Result: Done.
Commit SHA: `cb525e8`
Notes: Logs use `category=pos.access` and safe structured fields.

### ASUS-W7POS-006

Title: Fix CI startup validator and align task numbering
Status: Done
Base commit: `cb525e8`
Files touched: `scripts/check-pos-startup-win7-safe.ps1`, `docs/CODEX_ASUS_TASKS.md`
Goal: Align the GitHub startup validator with the current POS access startup flow and create this Asus task index.
Guardrails: Do not change login UI, login online/offline logic, target framework, or Win7/x86 constraints.
Commands: Startup validator plus the standard restore/check/build/test/selftest sequence.
Smoke: Static validator coverage; no WPF UI change in this task.
Result: Done. Required restore, checks, solution build, Core tests, CLI selftest, and WPF x86 build passed locally.
Commit SHA: this task commit.
Notes: Replaces legacy `OperatorLoginDialog`/`TryOnlineBootstrapFirstRunAsync` expectations with `PosOnlineFirstLoginDialog` and POS access markers.

### ASUS-W7POS-007

Title: Quick operator switch and permission-denied elevation UX
Status: Planned
Base commit: TBD
Files touched: TBD
Goal: Add a lightweight operator switch path for locked/permission-denied flows without restoring the old double startup login.
Guardrails: Startup remains the single POS access dialog. Keep WPF `net48`, x86, Windows 7 safe APIs, no WinUI/WebView2, and no PIN/password in logs.
Commands: Dialog standards, architecture boundaries, unified login UX, login logging check, WPF x86 build, targeted smoke.
Smoke: Switch operator from the shell, permission denied elevation from Users/Roles, Database maintenance, Products, and retry of the original action where safe.
Result: Planned.
Commit SHA: TBD.
Notes:
- `Change / Lock` should open a lightweight `OperatorSwitchDialog`.
- `OperatorSwitchDialog` should use active local or mirrored operators for the current shop.
- Fields: operator combo/list and PIN/password.
- Primary CTA: `Switch`.
- Secondary CTA: `Connect different shop / POS access`.
- Permission-denied UI for Users/Roles, Database maintenance, Products, and similar gated actions should show `Permission denied` plus `Switch operator`.
- After a successful switch, optionally retry the requested action when it is deterministic and safe.
- Operator switch logging should include a safe `attemptId` and result/stage fields, never PIN/password.
