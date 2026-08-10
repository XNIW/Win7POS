# Win7POS physical Windows 7 and hardware acceptance — 2026-08-09

## Disposition

- Classification: `BLOCKED_EXTERNAL`.
- Production certification: `NOT_QUALIFIED`.
- Acceptance code SHA: `b6a92b0d8f6d26ceee4f78a29f39d5862e8ef3ef`.
- Branch: `codex/win7-physical-hardware-final-release-20260809`.
- Baseline: `origin/main=26e1fd1db0d88bb42f6ecd85ff48717b3d8c4dbe`.
- This execution completed the available repository, Windows build-host,
  synthetic smoke, packaging, public HTTPS, and focused diff-security work. No
  physical Windows 7 device, hardware station, authenticated QA shop/account,
  production endpoint, or production code-signing certificate was available.
- No physical, authenticated-staging, installer-lifecycle, signing, or protected
  tag PASS is inferred from fixtures, static gates, Windows 11, or historical
  evidence.

## Build host and isolated paths

| Field | Observed value |
| --- | --- |
| Build host | Microsoft Windows 11 Home Single Language `10.0.26200` build `26200`, 64-bit |
| CPU / RAM | Intel Core Ultra 7 255H, 16 logical processors / 16,497,893,376 bytes |
| Display | Intel Arc 140T, 2880x1800; this is not the physical Win7 DPI matrix |
| .NET Framework | Release key `533509` (.NET Framework 4.8 family) |
| SDK | `C:\Dev\dotnet10\dotnet.exe`, `10.0.301` |
| App data | `C:\POSData\Win7FinalAcceptance\data` |
| Evidence | `C:\POSData\Win7FinalAcceptance\evidence` |
| Logs | `C:\POSData\Win7FinalAcceptance\evidence\logs` |
| Screenshots | `C:\POSData\Win7FinalAcceptance\evidence\screenshots` |

The physical Windows 7 edition/build, architecture, CPU/RAM/disk, .NET 4.8
release, VC++ x86 runtime, display/DPI, monitor, scanner, Xprinter, driver,
port, drawer, default printer, and spooler state are `NOT_OBSERVED` in this
execution.

## Repository protection

- The primary checkout remained on `main` and was not reset, checked out,
  stashed, or patched.
- Protected local file:
  `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml`.
- File SHA-256:
  `82869ACF8347213B50F609C79F74D5A213CF5C441BE72D45D4D5A7AB970987B6`;
  it matches the supplied protected hash.
- Binary patch:
  `C:\POSData\Win7FinalAcceptance\evidence\primary-checkout-protection\PosOnlineFirstLoginDialog-primary-dirty-20260809.patch`.
- Patch SHA-256:
  `47172902967E16C4D909170F6F5E693A571C54DE82CE7B6C7B18A1AE34538280`.
- PR #88 Storefront v1 was inspected as separate draft scope and was not merged,
  cherry-picked, or used to classify the current POS release.

## Automated validation

| Area | Result | Evidence / qualification |
| --- | --- | --- |
| Locked restore | `PASS` | Eight projects restored with SDK 10.0.301. |
| Required gates | `PASS 47/47` | `logs/23-post-remediation-required-gates.log`. |
| Dialog standards | `PASS 34/34` | Same log; no dialog file was changed. |
| Architecture/UI/i18n/supplier static gates | `PASS` | Same log. |
| Solution and WPF x86 build | `PASS` | 0 warnings, 0 errors; `logs/24-post-remediation-build.log`. |
| Core/Data automated baseline | `PASS 991/991` | Tree-identical trusted PR #92 prebuilt binaries; no skips. This is not relabelled as a fresh final-SHA run. |
| Fresh final-source MSTest | `BLOCKED_HOST_POLICY` | Build succeeds; MSTest discovery is blocked by Windows Application Control `0x800711C7`; `logs/22-post-remediation-focused-tests.log`. Expected inventory is 992 after the added regression. Exact-SHA CI is required. |
| CLI selftest | `PASS` | Tree-identical PR #92 binaries, isolated data. |
| Backup/restore selftests | `PASS` | Normal, fault/cancellation/recovery, and 32/128/512 MiB profiles; `logs/08-cli-same-tree-backup-restore*.log`. |
| UI smoke | `PASS` | Canonical patched run: 47 screenshots, Release net48/x86; `screenshots/ui-ux-final-closeout-patched-1`. |
| Supplier XLSX/XLS smoke | `PASS` | Synthetic WPF smoke only. |
| Product image and Products 100k dispatcher | `PASS` | Synthetic WPF smoke only. |
| Lifecycle/customer-display manager | `PASS` | 20 lifecycle cycles, 20 dialogs, 50 display and 50 manager cycles; synthetic only. |
| Authorization lease | `PASS` | Fail-closed dynamic/restart/capacity smoke, `hardwareEffects=0`. |
| Bounded logging | `PASS` | Queue high-water 256; intentional low-priority INFO shedding; no unbounded growth observed. |
| i18n validation | `PASS_PLAN_ONLY` | PlanOnly plus Windows 11 launch; physical language/DPI review remains blocked. |

## Release candidate

The branch candidate was built from a clean worktree by
`scripts/win7pos/windows/build-release-x86.ps1 -BuildInstaller`.

| Field | Value |
| --- | --- |
| Version | `1.0.0-dev.b6a92b0d8f6d` |
| Commit | `b6a92b0d8f6d26ceee4f78a29f39d5862e8ef3ef` |
| Platform / target | `x86` / `net48` |
| Installer | `Win7POS-1.0.0-dev.b6a92b0d8f6d-Setup.exe` |
| Installer SHA-256 | `FC51AFE79ECE5045361D18BC8FD6D09099ABADE735EAA634BB7127B57A06A758` |
| Installer evidence | `C:\POSData\Win7FinalAcceptance\evidence\release\branch-rc-b6a92b0\installer` |
| Payload validation | `PASS`; exact inventory, x86 executable/native SQLite, CLR v4/.NET 4.8, no source/PDB/DB/CLI/secret/production config |
| Authenticode | `NotSigned` |
| RFC3161 | `NOT_AVAILABLE` |
| Protected tag | `NOT_CREATED` |

No code-signing EKU certificate with an authorized private key was found in
CurrentUser/My or LocalMachine/My. No private key was exported or generated.

## Physical Windows 7, UI, and installer matrix

| Scenario | Result | Evidence |
| --- | --- | --- |
| Win7 SP1 prerequisites script on target | `BLOCKED_EXTERNAL` | No physical target. |
| Clean install / first launch / restart / reboot | `BLOCKED_EXTERNAL` | No physical target. |
| Uninstall / reinstall / upgrade / data contract | `BLOCKED_EXTERNAL` | Installer was built but not run on Win7. |
| Offline and online startup / SQLite native x86 load | `BLOCKED_EXTERNAL` | Not observed on Win7. |
| 1024x768, 1280x720, 1366x768 | `BLOCKED_EXTERNAL` | Automation screenshots are not physical acceptance. |
| 125% DPI and workstation DPI | `BLOCKED_EXTERNAL` | Not physically observed. |
| Keyboard-only, Enter/Esc, tab order, scanner focus | `BLOCKED_EXTERNAL` | Static/automation coverage exists; physical observation absent. |
| en / it / es / zh-CN / persistence / CJK rendering | `BLOCKED_EXTERNAL` | PlanOnly and Windows 11 launch only. |
| Runtime surfaces listed in the task | `BLOCKED_EXTERNAL` | No physical acceptance session. |

## Hardware matrix

| Hardware area | Result | Qualification |
| --- | --- | --- |
| Scanner, including 100-scan sequence and focus recovery | `BLOCKED_EXTERNAL` | No scanner connected; no synthetic result promoted. |
| Xprinter / 58–80 mm / accents / cutter | `BLOCKED_EXTERNAL` | No Xprinter or driver station available. Historical Epson rows 17–25 remain unchanged and are not treated as Xprinter/Win7 proof. |
| Spooler offline/stop/restart/queue recovery | `BLOCKED_EXTERNAL` | Not physically executed. |
| Cash drawer transactional matrix | `BLOCKED_EXTERNAL` | No impulse was sent. Historical rows 15 and 19–22 remain limited to their recorded scenarios. |
| Customer display / dual monitor / hot-plug | `BLOCKED_EXTERNAL` | Synthetic manager/lifecycle smoke passed; physical behavior unobserved. |

## Staging and business workflow matrix

- Public target:
  `https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev`.
- Builder HTTPS probe: `PASS`; valid TLS on the Windows 11 build host, root HTTP
  307, shipping API routes answered HTTP 405 to HEAD with certificate verify 0.
- Physical Windows 7 TLS: `BLOCKED_EXTERNAL`.
- QA shop/account/staff: `NOT_SUPPLIED`; no credential or shop identifier was
  read, stored, or logged.
- No documented executable named Run 5 exists in the allowed current acceptance
  documents/scripts; no command was invented.
- Authenticated staging is additionally blocked by the recorded schema
  reconciliation gap: source 72 migration IDs, staging history 79, common 71,
  with eight remote-only IDs lacking the required provenance/recovery manifest
  and verified backup. No migration or staging data was mutated.

| Scenario group | Result |
| --- | --- |
| Login/session/revocation/DPAPI | `BLOCKED_EXTERNAL` |
| Full catalog through `HasMore=false` | `BLOCKED_EXTERNAL` |
| Incremental create/update/price/stock/tombstone | `BLOCKED_EXTERNAL` |
| Offline/reconnect/checkpoint/idempotency | `BLOCKED_EXTERNAL` |
| Cash/card/mixed and sale ACK-loss replay | `BLOCKED_EXTERNAL` |
| Refund/void/daily close/business-date midnight | `BLOCKED_EXTERNAL` |
| Supplier import staging outbox/ACK outcomes | `BLOCKED_EXTERNAL` |
| Realistic UI backup/restore/reconnect reconciliation | `BLOCKED_EXTERNAL` |

## Performance evidence

Catalog results are net48/x86 on the same Windows 11 host, one warm-up plus
three measured samples. They were captured after the page-preload performance
fix and before the final static/security remediation. The final source and
benchmark executable build successfully, but WDAC blocks execution of the
fresh binary. Exact final-SHA performance therefore remains a CI requirement,
not an inferred PASS.

| Metric | Dataset | Iterations | Median | p95 | Peak memory | Baseline delta | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| Paged full catalog | 2,000 products/prices | 3 | 209.476 ms | 210.714 ms | 53,284,864 B WS / 35,135,488 B private | -35.1% vs 322.585 ms | `PASS_SNAPSHOT` |
| Paged full catalog | 19,763 products/prices | 3 | 2,067.446 ms | 2,112.038 ms | 56,188,928 B WS / 39,223,296 B private | -89.5% vs regressed 19,656.410 ms; +3.0% vs pre-regression 2,007.346 ms | `PASS_SNAPSHOT` |
| Paged full catalog | 100,000 products/prices | 3 | 19,041.306 ms | 19,113.277 ms | 60,747,776 B WS / 43,360,256 B private | no same-host pre-change baseline | `PASS_SNAPSHOT` |
| Backup | 32 MiB | 5 | 119.161 ms | 131.49 ms | not recorded | no comparison baseline | `PASS` |
| Restore | 32 MiB | 5 | 323.301 ms | 379.45 ms | 7,815,480 B median allocated | no comparison baseline | `PASS` |
| Backup | 128 MiB | 5 | 386.100 ms | 419.36 ms | not recorded | no comparison baseline | `PASS` |
| Restore | 128 MiB | 5 | 942.725 ms | 988.54 ms | 7,680,552 B median allocated | no comparison baseline | `PASS` |
| Backup | 512 MiB | 5 | 1,413.972 ms | 1,498.44 ms | not recorded | no comparison baseline | `PASS` |
| Restore | 512 MiB | 5 | 3,230.523 ms | 3,421.61 ms | 7,680,232 B median allocated | no comparison baseline | `PASS` |

Cold/warm startup, login, live staging catalog, scan-to-cart p50/p95, Products
search/page p50/p95, Payment, physical print handoff, live supplier import,
handle/GDI/thread soak, network transitions, controlled restarts, and hardware
fault injection were not measured on the physical target and remain
`BLOCKED_EXTERNAL`.

## Findings and fixes

| Finding | Resolution | Commit / regression |
| --- | --- | --- |
| UI closeout evidence output was coupled to the QA data directory | Separate evidence root from QA database selection | `6709096f66ddabe849e214136b4ce82a9927f1b2`; dynamic 47-screenshot UI run |
| Catalog protected-product query ran once per product, causing a 19.7 s regression at ~20k rows | One protected-product preload per page plus diagnostics | `b6a92b0d8f6d26ceee4f78a29f39d5862e8ef3ef`; page/count assertions |
| Fallback writer could reintroduce the per-product query; security diff finding P3/low | Pass explicit completed-lookup state and guard the writer | same commit; 1,000-row legacy-rebind regression and strengthened gate |
| .NET Unicode case comparison did not match SQLite ASCII-only `NOCASE` | ASCII-only key fold with ordinal dictionary | same commit; `SqliteNoCaseBarcodeKeyFoldsAsciiOnly` |
| Performance workflow accepted numeric prefixes | Require whitespace/end delimiter after exact counters | same commit; workflow gate correction |

The completed pre-fix focused security report is at
`C:\Users\xniw9\AppData\Local\Temp\codex-security-scans\Win7POS-win7-final-acceptance-20260809\6709096_20260810T005423-0400\report.md`.
It records one P3/low finding and two rejected security candidates. The code
finding and both correctness issues are remediated in `b6a92b0`; exact-SHA CI
remains the final executable regression authority because of the host WDAC
block.

## Evidence and redaction

- Logs: `C:\POSData\Win7FinalAcceptance\evidence\logs`.
- Screenshots retained: 95 automated PNGs across diagnostic/reproduction/pass
  runs; canonical UI PASS run 47; physical screenshots 0.
- Branch RC:
  `C:\POSData\Win7FinalAcceptance\evidence\release\branch-rc-b6a92b0`.
- Interim clean RC at `6709096`:
  `C:\POSData\Win7FinalAcceptance\evidence\release\clean-rc-ci-6709096-20260809`.
- Evidence manifest/hash: generated at final handoff under
  `C:\POSData\Win7FinalAcceptance\evidence\manifest`.
- Repository Gitleaks 8.30.1 worktree scan: 0 leaks.
- Binary screenshots and raw logs are not committed. The committed report uses
  no shop, staff, request payload, PIN, password, token, or production data.

## External backlog and blockers

- Before: 10/25 historical physical PASS rows; 15 open/partial rows.
- Newly closed: 0.
- After: 10/25 historical physical PASS rows; 15 open/partial rows.
- Exact blockers:
  - `PHYSICAL_WINDOWS_7_SP1_TARGET_AND_OPERATOR`;
  - `SCANNER_XPRINTER_DRAWER_CUSTOMER_DISPLAY_DUAL_MONITOR_STATION`;
  - `AUTHENTICATED_ISOLATED_STAGING_QA_ACCOUNT_AND_SHOP`;
  - `STAGING_SCHEMA_PROVENANCE_RECOVERY_MANIFEST_AND_VERIFIED_BACKUP`;
  - `PRODUCTION_HTTPS_ENDPOINT_AND_WIN7_TLS_VALIDATION`;
  - `REAL_CODE_SIGNING_CERTIFICATE_AND_RFC3161`;
  - `INSTALL_UPGRADE_UNINSTALL_AND_SIGNED_FINAL_ARTIFACT_SMOKE`;
  - `PROTECTED_SEMANTIC_VERSION_TAG_AFTER_EXTERNAL_MATRIX_PASS`.

These blockers prevent `PRODUCTION_100_PERCENT_COMPLETE` and any installer or
hardware qualification. The maximum demonstrated classification in this
execution is `BLOCKED_EXTERNAL`.
