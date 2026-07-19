# Win7POS receipt surface inventory

Date: 2026-07-18

Branch: `codex/hardware-epson-tm-t60-20260717-161122`

This inventory distinguishes thermal paper receipts, the dedicated daily-close
report, fiscal documents and non-paper customer-display projections. Normal
sales receipts use one immutable persisted-value snapshot and one renderer.

| Surface | Preview builder | Print builder | Same text? | Authoritative source | Drawer | Automatic file copy | Lifecycle owner |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Payment preview | `SalesReceiptRenderModel` → `PosReceiptTextRenderer` | same renderer after commit | YES for the same snapshot/profile | current payment draft frozen at confirmation, then persisted sale/lines | preview NO; committed cash policy only after sale | NO | `PaymentViewModel.Dispose` |
| Final normal sale | `PosReceiptTextRenderer` | exact rendered string | YES | committed sale economics, lines and frozen shop snapshot | cash policy only; card-only NO | NO | POS workflow |
| Printer Settings fictitious sample | `PosReceiptTextRenderer` plus explicit no-sale marker | exact preview string | YES | non-persisted synthetic sale | NO | NO | `PrinterSettingsViewModel.Dispose` |
| Sales Register historical preview | lazy `GetReceiptPreviewBySaleIdAsync` → `PosReceiptTextRenderer` | selected `DetailReceiptPreview` | YES | SQLite `sale` + `sale_lines` + frozen `receipt_shop_snapshot` | NO | NO | `SalesRegisterDialog`/`SalesRegisterViewModel.Dispose` |
| Historical reprint / Print last | `PosReceiptTextRenderer` | exact preview string | YES | same persisted sale snapshot | NO | NO | POS workflow |
| Refund receipt | `PosReceiptTextRenderer` with reversal header | exact rendered string | YES | persisted refund economics and lines | NO | NO | refund/POS workflow |
| Void receipt | `PosReceiptTextRenderer` with reversal header | exact rendered string | YES | persisted void economics and lines | NO | NO | refund/POS workflow |
| Daily close, current or historical day | `DailyCloseReceiptTextRenderer` | exact `SummaryReceiptPreview` or same dedicated renderer | YES | existing `DailySalesSummary` calculations | NO | NO | `DailyReportViewModel.Dispose` |
| Multi-day daily summary | shared `ReceiptTextLayout` aggregate string | exact aggregate string | YES | persisted daily summaries | NO | NO | `DailyReportViewModel.Dispose` |
| Boleta direct print | `PaymentViewModel` fiscal preview | exact `FiscalPreviewText` sent to configured spooler | YES | current payment draft, selected boleta number and cached shop data | NO | NO | payment workflow |
| Daily CSV export | export preview/status only | explicit user-chosen export | separate surface | daily summaries | NO | Explicit export retained | Daily Report |
| Customer display | `CustomerDisplayProjection` | not printed | not a paper receipt | current cart/payment projection | NO | NO | `CustomerDisplayManager.Dispose` |

## Invariants

- `SalesReceiptRenderModel` copies mutable `Sale`, `SaleLine` and
  `ReceiptShopInfo` values into read-only snapshots before rendering.
- Payment, final print, history, refund/void and reprint use
  `PosReceiptTextRenderer`; no paper-receipt preview recalculates product prices.
- Historical shop data uses the frozen snapshot when present. The documented
  current-shop fallback remains only for legacy rows that predate the snapshot.
- Daily close is intentionally a separate document, but it shares visible-width,
  wrapping, padding, two-column and separator primitives with sales receipts.
- The selected printer profile controls 32/42 columns. Tests reject any physical
  line whose visible width exceeds the selected profile.
- `WindowsSpoolerReceiptPrinter` only submits to the spooler. It has no receipt
  or boleta text/image/PDF archive writer.
- Legacy automatic-copy setting keys are no longer read or written, so an old
  value cannot reactivate file output. Existing user files are not scanned or
  deleted.
- The automatic fiscal PDF writer and its PDF library are absent. The legacy
  `sales.pdf_printed` column remains only as a compatibility status flag for a
  successfully printed boleta; it is not a path or stored document.
- Explicit CSV exports and database backups remain separate user actions.

## Automated checkpoint

- Required source-and-release gates: `35/35 PASS`, including direct-boleta/no-PDF checks in
  `check-pos-receipt-surface-consistency.ps1`.
- Core tests: `298/298 PASS`, zero skipped.
- WPF net48/x86 and UI harness x86 isolated builds: PASS with zero warnings and
  zero errors; neither output contains `PdfSharp` or a generated PDF.
- Focused x86 runtime harness: `fiscalDirectPrintNoArchivePass=True` for cash,
  card-only and simulated spooler failure; the QA data root contained zero PDF
  files and no export directory after the run.
- The same configured `ReceiptPrintOptions.Copies` value is used for receipt and
  boleta jobs. Invalid copy counts fail before a spooler task is created; the
  supported range is one through three.

## Physical receipt-surface addendum — 2026-07-19

The dedicated no-database physical harness submitted exactly six sequential,
awaited, one-copy jobs to `EPSON TM-T60 Receipt` on the Windows 11 QA host:
fiscal 32 columns, fiscal 42 columns, receipt original, byte-identical receipt
reprint request, daily close 32 columns and daily close 42 columns. Every slip
was marked `QA - PRINTER TEST`, `NON FISCAL`, `NO SALE SAVED` and `NO DRAWER`.

The atomic manifest records `SUBMITTED_JOBS=6`, `DRAWER_CALLS=0`, absent database
artifacts and identical request hashes for jobs 3 and 4. The operator visually
confirmed exactly six legible/cut slips, correct widths and codes, identical
original/reprint output, no extras and a closed drawer. The queue returned
`Normal` with zero jobs. Evidence is retained outside Git at
`C:\Dev\Win7POS-QA\hardware\Epson-TM-T60\20260719-pr7-final-01\physical-printer-qa.txt`,
SHA-256 `F635BB9DC6FC45A8DD5A881CDA1EB0E1AB3CF5288542569A43395279699FE1DA`.

This closes the PR #7 receipt-surface physical merge gate. It does not constitute
a physical Windows 7 SP1 run, which remains `NOT_RUN_WIN7_PHYSICAL`.
