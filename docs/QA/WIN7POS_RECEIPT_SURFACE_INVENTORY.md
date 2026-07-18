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
| Fiscal PDF / boleta | `FiscalPdfService` | explicit fiscal document workflow | separate surface | persisted fiscal/sale data | NO | Explicit fiscal behavior retained | fiscal workflow |
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
  text/image/PDF archive writer.
- Legacy automatic-copy setting keys are no longer read or written, so an old
  value cannot reactivate file output. Existing user files are not scanned or
  deleted.
- Fiscal PDFs, explicit exports and database backups are separate and remain
  unchanged.

## Automated checkpoint

- Required source gates: `32/32 PASS`, including
  `check-pos-receipt-surface-consistency.ps1`.
- Solution Release build: PASS, 0 errors; only offline NuGet vulnerability-feed
  warnings.
- WPF net48/x86 and UI harness x86 builds: PASS, 0 warnings/errors.
- Receipt alignment harness: PASS for cash/card/mixed, line/cart discounts,
  EN/ES/IT/ZH and 32/42 columns.
- Lifecycle run after the disposal fix: 20 Daily Report, 20 Sales Register and
  20 Printer Settings cycles; zero residual windows/ViewModels; language and
  display handlers returned to baseline; private bytes and handles were not
  monotonic.
- The current host's Application Control policy later blocked newly rebuilt
  unsigned test/runtime binaries (`0x800711C7`). The solution and focused
  checkers compile, while the final MSTest/CLI rerun must be taken from CI or the
  trusted Release Pack rather than misreported as a product failure.

Physical post-change reprint and daily-close output remain the final merge gate.
