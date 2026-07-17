# Win7POS incremental sync drain closeout

Date: 2026-07-17

Scope: close the remaining incremental-sync gaps without changing outbox
payloads, hashes, idempotency, refund/void economics or periodic full-refresh
policy.

## WIN7POS_SYNC_DRAIN_RESULT

- branch: `codex/win7pos-sync-drain-closeout-20260717`, created from
  `feature/win7pos-customer-display-exit-ux-20260716-232840`
- baseline HEAD: `6c321d6edeb9e1b6daea91ec5c694c25f787de8e`
- HEAD: branch tip containing this closeout report
- baseline tests: PASS, 237/237, zero skipped
- final tests: PASS, 248/248, zero skipped
- canonical gates: PASS, 30/30
- scheduler always active: PASS after normal POS opening and after successful
  recovery exit; safe-start, active recovery and per-shop/session single-flight
  exclusions remain in place
- idle polling seconds: PASS, bounded randomized 24-36 seconds
- 60-sale backlog: PASS, deterministic 25-row bounded drain
- pending after run 1: 35
- pending after run 2: 10
- pending final: 0
- retry behavior: `ContinueBackground`; ordinary retry does not block the register
- in-progress behavior: active and stale rows remain unresolved and select
  `ContinueBackground` until ACK/retry reclamation removes them
- blocked behavior: `Blocked` takes priority when `failed_blocked > 0`
- product delta without foreground: PASS; the scheduler remains alive after a
  completely successful start-of-day and continues periodic incremental pulls
- no periodic full: PASS; healthy `Periodic` policy evaluation selects
  `Incremental` with no full reason
- WPF net48/x86: PASS, zero warnings and zero errors
- CLI selftest: PASS, including refund/void checks
- main changed: NO
- remaining staging tests: authenticated Admin Web delta/ACK exercise, network
  recovery timing, physical Windows 7 SP1 run, printer/cash-drawer/customer-display
  hardware checks
- recommendation: `READY_FOR_STAGING`

## Implementation evidence

`StartOfDaySalesDrainPolicy` is pure Core code and decides only from persisted
outbox counters: blocked first, then pending/retry/in-progress background work,
then complete at zero. `PosStartOfDaySyncService` refreshes the outbox after the
sales attempt and derives completion from that policy instead of inferring it
from the absence of an exception.

`MainWindow` starts the existing adaptive scheduler unconditionally in the normal
POS path, including when start-of-day already completed. The existing scheduler
coordinator, dirty-bit coalescing, authentication stop, offline recovery and
sale-safe catalog rules are unchanged.

## Validation commands

All requested commands completed successfully:

```powershell
& "C:\Dev\dotnet10\dotnet.exe" restore Win7POS.slnx
pwsh -NoProfile -File scripts/check-required-gates.ps1
pwsh -NoProfile -File scripts/check-pos-start-of-day-sync.ps1
pwsh -NoProfile -File scripts/check-pos-sales-sync.ps1
pwsh -NoProfile -File scripts/check-pos-catalog-pull.ps1
& "C:\Dev\dotnet10\dotnet.exe" build Win7POS.slnx -c Release --no-restore
& "C:\Dev\dotnet10\dotnet.exe" test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-build --no-restore
& "C:\Dev\dotnet10\dotnet.exe" run --project src\Win7POS.Cli\Win7POS.Cli.csproj -c Release --no-build --no-restore -- --selftest --keepdb
& "C:\Dev\dotnet10\dotnet.exe" build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore
```

The automated results support staging readiness; they do not substitute for the
remaining authenticated and physical-hardware staging checks listed above.

## Final certification — 2026-07-17

The final review corrected one recovery gap: endpoint-offline results now retain
bounded quiet polling even if Windows reports the NIC continuously up, and a
deterministic recovery test verifies backoff reset after server recovery. Final
local evidence is 30/30 gates, 249/249 tests, zero skipped, WPF net48/x86 and CLI
selftest PASS. The deterministic 60-sale policy sequence remains `60 → 35 → 10 →
0`; it is not a substitute for the mandatory authenticated staging fixture and
remote duplicate/ACK verification. Task status remains `NOT_DONE` and main is
unchanged.
