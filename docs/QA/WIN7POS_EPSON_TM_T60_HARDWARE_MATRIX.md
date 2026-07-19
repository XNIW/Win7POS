# Win7POS Epson TM-T60 (M249B) hardware matrix

Date: 2026-07-17

Host: ASUS, Windows 11 Home Single Language `10.0.26200`, x64

Printer: Epson TM-T60, label code M249B, USB, 80 mm thermal paper
QA data: isolated under `C:\Dev\Win7POS-QA\hardware\Epson-TM-T60\20260717-161122`; the manual drawer command used a fresh evidence root under the Codex visualization workspace and created no `pos.db`. Its retained manifest is `cash-drawer-evidence\2026-07-17-pulse-01\drawer-pulse-manifest.md`, SHA-256 `9DE69D8A08DE1B0EA90100C1DE31A3EE01DFA7D21685BEF671F5C1B0F423DBDC`. The retained final lifecycle result is `pay-receipt-alignment-audit\lifecycle-result.txt`, SHA-256 `41AFE5F5757CD04CFB221E7D0FD45091C08E7A22187DAF381DCCA4A8790E196C`. No production database or transaction was used.

## Driver and queue evidence

| Item | Result | Evidence |
| --- | --- | --- |
| Physical interface | PASS | USB device `USB\VID_04B8&PID_0E25\…` (instance suffix redacted). |
| Official package | PASS | Epson APD 5.13 package `APD_513_T60_SCWM.zip`, 41,640,543 bytes. |
| Package SHA-256 | PASS | `B25BDBA2F4E8A618C6041D8BD102A938991B53AC332AD3F421EB11219D2461AA`. |
| Installer signature | PASS | Authenticode `Valid`, signer `SEIKO EPSON CORPORATION`. |
| Installer SHA-256 | PASS | `06596CEA4994EC051B13B148BDA94B061774232C74EAD23F821D0BFB0B1269D5`. |
| Supported model | PASS | Package README explicitly lists TM-T60. |
| Current OS | PASS | Package README explicitly lists Windows 11 64-bit. |
| Windows 7 target | DOCUMENTED_NOT_PHYSICALLY_RUN | Package README lists Windows 7 SP1 32/64-bit. |
| Reboot | PASS | Installer did not request a reboot. |
| Queue | PASS | `EPSON TM-T60 Receipt`. |
| Driver | PASS | `EPSON TM-T60 Receipt5`; installed INF driver version `5.12.0.0` (APD package version `5.13.0.0`). |
| Port | PASS | `ESDPRT001`, Epson port description `USB TM-T60`. |
| Architecture | PASS | x64 Windows driver installed; Win7POS remains net48/x86. |
| Queue sharing | PASS | Not shared and not published. |
| Paper | PASS | `Roll Paper 80 x 297 mm`, portrait, 203 dpi. |
| Default printer preserved | PASS | Previous default `Microsoft Print to PDF` restored after the Windows sample. |

Official sources:

- Epson TM-T60 support: <https://www.epson.com.cn/services/supportproduct.html?p=fea4d2ca37ac4f51acb7d10c16d91e6d>
- Epson APD 5.13 detail: <https://www.epson.com.cn/drive/7b6e0cdfc4aa4073947467eda7b8359a.html?productId=fea4d2ca37ac4f51acb7d10c16d91e6d>
- Official package: <https://eposs.epson.com.cn/EPSON/assets/resource/Download/Service/driver/sd/TM-T60/APD_513_T60_SCWM.zip>
- Epson ESC/POS `ESC p` reference: <https://download4.epson.biz/sec_pubs/pos/reference_en/escpos/esc_lp.html>

No Epson ZIP, EXE, INF, PDF, driver binary or photograph is stored in Git.

## Physical output matrix

`PASS_PHYSICAL` means the operator observed the real paper or drawer action. A successful spooler API call alone is not physical evidence.

| Scenario | Status | Physical evidence / notes |
| --- | --- | --- |
| Epson utility / driver test | PASS_PHYSICAL | Operator confirmed successful test print after APD installation. |
| Windows printer test | PASS_PHYSICAL | Printed through the real Epson queue. |
| Notepad short sample | PASS_PHYSICAL | `Win7POS Epson / TM-T60 USB - prova / Notepad OK`. |
| Notepad 80 mm multiline sample | PASS_PHYSICAL | ASCII, Italian and Spanish accents, CLP totals, long product, 42/48 rulers and automatic cut visible in the supplied photograph. |
| Accents | PASS_PHYSICAL | `caffè`, `più`, `città`, `perché`, `qualità`, `información`, `acción`, `corazón`, `niño`, `pingüino` legible. |
| Feed / crop / A4 scaling | PASS_PHYSICAL | Correct thermal feed; no crop, blank extra page or A4 scaling observed. |
| Cutter | PASS_PHYSICAL | Automatic cut observed. |
| Queue drain | PASS | Queue returned empty and normal after Windows samples. |
| 42 vs 48 characters | OBSERVED_WRAP | Both Notepad rulers wrap with that sample font. No global Win7POS width change was inferred from this result. |
| Win7POS diagnostics UI | PASS_QA_RENDERED | Printer Settings, payment preview and POS footer rendered from the Release x86 build; targeted presentation/layout checks pass at 1280x720 and 1024x600. This is not a physical Win7 claim. |
| Win7POS test print | PASS_PHYSICAL | Operator confirmed the complete Win7POS fictitious receipt printed correctly, stayed within the roll and cut automatically. |
| QA cash receipt | PASS_PHYSICAL | QA sale `VMRQI73CRZQ6` printed completely, cut automatically and opened the drawer exactly once. The operator confirmed the paper and single opening. |
| QA card receipt | PASS_PHYSICAL_NO_DRAWER | QA card-only sale `VMRQIA8J5KE3` printed completely; the operator confirmed that the drawer stayed closed. |
| Receipt reprint | PASS_PHYSICAL_NO_DRAWER | One reprint of persisted cash sale `VMRQI73CRZQ6` printed correctly; no sale, movement or outbox duplicate was created and the operator confirmed that the drawer stayed closed. |
| Printer off / paused after commit | PASS_COMMIT_BEFORE_PRINT_NO_DUPLICATE | Card-only sale `VMRQIK583IXD` committed while the queue was paused, reported a retryable print failure, and remained a single sale/line/movement/outbox entry. After resume, one `Print last` produced the receipt; the operator confirmed correct paper output and no drawer opening. |
| Final direct fiscal 32/42 addendum | PASS_PHYSICAL_NO_DRAWER_2026-07-19 | The dedicated no-database harness submitted one 32-column and one 42-column fiscal QA slip. The operator confirmed legible/cut output and correct widths/codes; both carried non-fiscal/no-sale/no-drawer warnings. |
| Final exact receipt original/reprint | PASS_PHYSICAL_IDENTICAL_NO_DRAWER_2026-07-19 | Jobs 3 and 4 had identical text and request hashes. The operator confirmed identical physical slips, no extra copy and a closed drawer. |
| Final daily close 32/42 addendum | PASS_PHYSICAL_NO_DRAWER_2026-07-19 | One 32-column and one 42-column daily-close QA slip printed in the same awaited sequence. The operator confirmed correct widths, legible/cut output and no drawer opening. |

Photo evidence is retained outside Git as
`windows-notepad-80mm-pass.png`, SHA-256
`B283D2669FEC7C58EFB46365F2AE16B95BD3D15343C49694BADDFD20AA6AC4AD`.

## Cash drawer matrix

No row below is marked PASS until a physical opening or non-opening is observed.

| Scenario | Status | Required observation |
| --- | --- | --- |
| Cable in printer DK/DKD (not Ethernet/telephone) | OPERATOR_REPORTED_CONNECTED_BEFORE_PULSE__EXACT_DK_DKD_NOT_DOCUMENTED | The operator stated that the drawer was connected and authorized the test; the exact cable path was not independently documented. |
| Epson utility drawer test | PENDING_PHYSICAL | One utility action, if the installed utility exposes the supported test. |
| Production-service pin 2 (`27,112,0,25,250`) | PASS_PHYSICAL_SINGLE_OPEN_2026-07-17 | One direct production-code `TestCashDrawerAsync` submission returned successfully at 2026-07-17 21:36 local; the operator later explicitly confirmed that this single pulse opened the drawer exactly once. One matching log entry was retained, pre/post queue observations were `Normal`/0, and no `pos.db` was created. This did not exercise the authenticated Printer Settings UI. No retry was sent. |
| Printer Settings > Test drawer (authenticated UI) | PENDING_AUTHENTICATED_PHYSICAL | Permission gate, UI state/status and settings persistence remain unproven; do not use this row as authorization for another pulse. |
| Win7POS manual pin 5 (`27,112,1,25,250`) | NOT_NEEDED_NOT_SENT | Pin 2 physically passed; pin 5 must not be sent. |
| Cash-only QA sale | PASS_PHYSICAL_SINGLE_OPEN_2026-07-18 | Sale committed, receipt printed and cut, and the operator confirmed exactly one drawer opening. The application log contains one matching drawer event. |
| Card-only QA sale | PASS_PHYSICAL_NO_OPEN_2026-07-18 | Receipt printed and the operator confirmed that the drawer did not open. |
| Drawer failure after committed sale | PASS_SOFTWARE_NO_DUPLICATE_PHYSICAL_NO_OPEN_2026-07-18 | The paused-queue card sale committed before the print failure, exposed retry guidance, and was reprinted once after resume without duplicates; the operator confirmed no drawer opening. |
| Drawer disconnected | PENDING_PHYSICAL | POS remains usable; card sale remains possible. |

## Automated safeguards

| Check | Result |
| --- | --- |
| Required source gates | PASS, 31/31 |
| Printer/cash-drawer safety gate | PASS |
| Driver-discovery gate | PASS |
| Solution Release build | PASS; only offline `NU1900` vulnerability-feed warnings |
| Core/Data automated tests | PASS, 260/260 outside sandbox; final functional edits remain WPF/harness-only |
| CLI selftest | PASS (`自检 PASS`) after final fixes |
| WPF net48/x86 build | PASS, 0 warnings and 0 errors |
| Dialog lifecycle | PASS on the final 20-cycle run: 20 Printer Settings cycles, 50 display windows/managers, zero residual/open windows or ViewModels, language/display handlers 0 → 0 and non-monotonic resource samples |
| New strict parser / single-flight harness | PASS in the final lifecycle run: printer command policy, selection binding and cash-drawer parser all true |
| Receipt renderer alignment | PASS; exact parity for cash, card and mixed payments, including line/cart discounts, in EN/ES/IT/ZH at 32/42 columns; shop data is frozen from preview through completed-sale output and the printer sample uses the same sale barcode path plus the no-sale marker |
| POS footer touch geometry | PASS; Pay matches the visible tools width and edge alignment at 1280x720 and 1024x600 |
| Manual drawer physical evidence | PASS_PHYSICAL; one production-code submission returned successfully and the operator confirmed exactly one opening; one matching log entry, pre/post queue `Normal`/0, isolated root contains no database; retained manifest hash above |
| Final receipt-surface physical addendum | PASS_PHYSICAL; six sequential one-copy jobs, 32/42 fiscal, exact original/reprint and 32/42 daily close; operator confirmed all six, no extras and no drawer opening; manifest SHA-256 `F635BB9DC6FC45A8DD5A881CDA1EB0E1AB3CF5288542569A43395279699FE1DA` |

The source now uses Win7 Unicode spooler APIs, bounded/cached discovery, strict
`ESC p` parsing and task-based single-flight test actions. A malformed non-empty
drawer command cannot silently fall back to a physical pulse.

## Current classification

`RECEIPT_SURFACE_ADDENDUM_PASS_WINDOWS7_AND_DISCONNECTED_DRAWER_OPEN`

Windows/APD/Notepad, the Win7POS fictitious receipt and the transactional cash,
card, reprint and paused/resumed-queue matrix are physically proven. Exactly one
transactional drawer opening was observed for cash; card and both reprint paths
kept it closed. The final no-database sequence also physically proves direct
fiscal 32/42, identical receipt original/reprint and daily close 32/42 with zero
drawer calls. Counts remained idempotent and the queue returned `Normal` with
zero jobs. The receipt-surface PR #7 merge gate is closed. The disconnected-drawer,
authenticated settings and physical Windows 7 rows remain open for full hardware
certification and are not represented as PASS.

## Publication

Implementation commit `7d1ef84` is published on
`codex/hardware-epson-tm-t60-20260717-161122`. Draft pull request
<https://github.com/XNIW/Win7POS/pull/7> targets `main`. The final unpublished
closure commit and exact-head CI remain required before merge; no force push or
rebase is permitted.
