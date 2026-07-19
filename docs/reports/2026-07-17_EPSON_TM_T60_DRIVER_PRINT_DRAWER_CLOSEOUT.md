# Epson TM-T60 driver, print and drawer partial closeout — 2026-07-17

## Scope and isolation

This hardware validation run follows PR-B on the independent branch
`codex/hardware-epson-tm-t60-20260717-161122`, based on
`origin/main` commit `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c`.
It does not contain the PR-B migration commits.

The host-side installer, manuals, checksums and photograph are retained under
`C:\Dev\Win7POS-QA\hardware\Epson-TM-T60\20260717-161122`. Drawer-pulse evidence
uses a separate fresh root under the Codex visualization workspace; its retained
manifest is `cash-drawer-evidence\2026-07-17-pulse-01\drawer-pulse-manifest.md`,
SHA-256 `9DE69D8A08DE1B0EA90100C1DE31A3EE01DFA7D21685BEF671F5C1B0F423DBDC`.
The final lifecycle result is retained as
`pay-receipt-alignment-audit\lifecycle-result.txt`, SHA-256
`41AFE5F5757CD04CFB221E7D0FD45091C08E7A22187DAF381DCCA4A8790E196C`.
That root contains no `pos.db`. The Git repository contains only source, tests
and redacted documentation; production data was not opened.

## Outcome so far

- Official Epson APD 5.13 was verified, installed and did not request reboot.
- The real USB queue is `EPSON TM-T60 Receipt`, driver
  `EPSON TM-T60 Receipt5`, port `ESDPRT001` (`USB TM-T60`).
- Epson APD/Windows and Notepad 80 mm output are physically PASS, including
  accents, feed and cutter.
- The operator also confirmed the Win7POS fictitious receipt printed completely
  and correctly with automatic cut.
- The operator confirmed the authenticated QA transactional matrix: cash printed
  and cut with exactly one drawer opening; card and reprint printed with the
  drawer closed; a card sale committed during a paused queue, exposed a retryable
  print failure, and reprinted once after resume with no duplicate or drawer open.
- Win7POS source now reads real queue/driver/port/state metadata through Unicode
  Windows 7 spooler APIs without WMI, WinRT or a new package.
- Discovery is background, single-flight, bounded to five seconds and retains
  only the last completed inventory on failure/timeout.
- Receipt/drawer operations resolve through that same bounded inventory; no
  synchronous discovery bypass remains.
- Printer Settings detaches localization and request handlers on close; its
  print/drawer tests are task-based single-flight operations.
- A non-empty malformed drawer command is rejected before the spooler. The
  settings cannot be saved with an invalid enabled drawer command.
- Printer Settings now exposes a clean two-column setup with a scaled receipt
  preview; that preview, the payment screen and completed-sale printing all use
  one shared text renderer for cash, card and mixed payment data, including
  lossless line/cart discount rows. The shop snapshot is frozen from preview to
  completed-sale output, and the fictitious print uses the normal sale barcode
  path.
- The POS `Pay` target matches the visible width and edges of the right-side
  touch tools while the one-line footer stays within 80 DIP at 1280x720 and
  1024x600.
- One pin-2 command was submitted by a direct call to production-code
  `TestCashDrawerAsync`, outside the authenticated Printer Settings UI. It
  returned successfully; one matching log entry was retained, pre/post queue
  observations were normal/empty, and the isolated root contains no database.
  The operator later explicitly confirmed that this single pulse opened the
  drawer exactly once. No retry or pin-5 pulse was sent.

## Review findings resolved

The final review initially found three defects:

1. operational print/drawer resolution bypassed the discovery timeout;
2. malformed drawer text could silently become the default physical pulse;
3. double-clicking a test could enqueue more than one operation.

All three were corrected. A later receipt-parity review also found and fixed
lossy discount-line mapping plus a `PaymentViewModel` localization subscription
leak. Focused gates and the complete 31-gate set pass.

## Validation evidence

| Validation | Result |
| --- | --- |
| `git diff --check` | PASS; only Git line-ending notices |
| `scripts/check-required-gates.ps1` | PASS, 31/31 |
| `scripts/check-pos-printer-cashdrawer-safety.ps1` | PASS |
| `scripts/check-pos-printer-driver-discovery.ps1` | PASS |
| `dotnet build Win7POS.slnx -c Release --no-restore -m:1 -nr:false` | PASS; four offline `NU1900` feed warnings, 0 errors |
| Core/Data tests | PASS, 260/260 outside sandbox; the final edits are confined to WPF/tests/scripts |
| CLI `--selftest --keepdb` | PASS after final edits (`自检 PASS`) |
| WPF Release net48/x86 | PASS, 0 warnings, 0 errors |
| UI smoke harness Release x86 build | PASS; offline `NU1900`, 0 errors |
| Dialog lifecycle 20-cycle run | PASS after final fixes: 20 Printer Settings cycles, 50 display windows/managers, zero residual/open windows or ViewModels, stable handlers and non-monotonic resources |
| Strict-parser/single-flight runtime rerun | PASS in the final lifecycle run; printer policy, selection binding and drawer parser all true |
| Receipt rendering alignment | PASS: cash/card/mixed plus line/cart discounts, EN/ES/IT/ZH, 32/42 columns; payment preview equals frozen-shop production output and settings test adds only the explicit no-sale marker/barcode identity |
| POS footer geometry | PASS at 1280x720 and 1024x600; Pay width/edges equal the visible tools panel |
| Printer Settings presentation | PASS_QA_RENDERED/PASS_AUTOMATED; preview and main settings visible, advanced/drawer/detected-printer sections progressively disclosed; authenticated persistence remains open |
| Manual drawer command | PASS_PHYSICAL: one production-code submission returned successfully and the operator confirmed exactly one opening; one matching log entry, pre/post `Normal`/0, no `pos.db`, no retry |

The first sandboxed Core/Data run reported four `UnauthorizedAccessException`
failures in `File.Replace`. The identical run outside the filesystem sandbox
passed 260/260, proving an environment restriction rather than a product
regression.

## Transactional matrix checkpoint — 2026-07-18

The matrix used the isolated QA root
`C:\POSData\Win7POS-QA\Win7POS-Epson-Transactional-20260718-104809` and the
Release x86 application. Persisted counts finished at 10 sales, 12 lines,
11 stock movements and 10 outbox rows, with no duplicate client sale IDs or
movement keys. The application log contains exactly one drawer event, belonging
to cash sale `VMRQI73CRZQ6`; the card, reprint and resumed-reprint paths added no
drawer event. The Epson queue returned `Normal`, zero jobs, with Spooler running
and automatic. The operator confirmed all four paper/drawer outcomes.

## Receipt-surface addendum closure — 2026-07-19

The dedicated no-database harness submitted one awaited six-job sequence to the
real Epson queue: direct fiscal 32/42, exact receipt original/reprint and daily
close 32/42. The operator confirmed all six legible/cut slips, correct widths and
codes, identical original/reprint output, no extras and no drawer opening. The
manifest recorded one copy per job, identical request hashes for jobs 3/4, zero
drawer calls, no database artifacts and a final normal/empty queue.

This closes the receipt-surface addendum and the PR #7 physical merge gate. The
following remain open and must not be represented as PASS:

- authenticated settings persistence across a real operator session;
- the separate authenticated Printer Settings UI test-drawer action and
  disconnected-drawer behavior;
- a physical Windows 7 SP1 run.

The accurate classification is
`RECEIPT_SURFACE_ADDENDUM_PASS_WINDOWS7_AND_DISCONNECTED_DRAWER_OPEN`, not full
hardware certification.

## Publication

Implementation commit `7d1ef84` was pushed on
`codex/hardware-epson-tm-t60-20260717-161122`. Draft pull request
<https://github.com/XNIW/Win7POS/pull/7> targets `main`; no merge or auto-merge
was requested. The open transactional-drawer and Windows 7 rows remain explicit review
constraints and must not be represented as PASS.
