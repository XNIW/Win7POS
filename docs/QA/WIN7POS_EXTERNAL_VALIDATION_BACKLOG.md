# Win7POS external validation backlog

## Status and scope

- Backlog status: `OPEN`.
- External validation status: `PARTIAL_PHYSICAL_PASS`.
- Software merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production/hardware certification: `OPEN` and `NOT_YET_CERTIFIED`.

Rows 15, 17 and 18 are the only rows declared physically PASS. Every other open
item is non-blocking for the owner-authorized software merge and remains
mandatory before production or full hardware certification.

| # | External validation item | Status | PASS declared | Software merge gate | Production/hardware certification |
| ---: | --- | --- | --- | --- | --- |
| 1 | Staging catalog full iniziale. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 2 | Staging incremental create/update/price/stock/tombstone. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 3 | Resume e network recovery reali. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 4 | Vendite cash/card/mixed. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 5 | Offline/reconnect e idempotenza server. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 6 | Refund e void reali. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 7 | Riconciliazione giornaliera. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 8 | BusinessDate/mezzanotte. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 9 | Windows 7 SP1 fisico. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 10 | Installer smoke Win7. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 11 | Dual-monitor Windows Extend. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 12 | Customer display fisico. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 13 | Scanner. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 14 | Xprinter. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 15 | Cash drawer manual pin-2 pulse. | `PASS_PHYSICAL_SINGLE_OPEN_2026-07-17` | `YES` | `NON_BLOCKING_OWNER_AUTHORIZED` | `PARTIAL_ONLY_TRANSACTIONAL_DRAWER_MATRIX_REMAINS` |
| 16 | Profili DPI e IT/EN/ES/ZH runtime. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 17 | Epson TM-T60 APD/Windows/Notepad 80 mm print, accents, feed and cutter. | `PASS_PHYSICAL_2026-07-17` | `YES` | `NON_BLOCKING_OWNER_AUTHORIZED` | `PARTIAL_ONLY_REMAINING_WIN7POS_DRAWER_AND_WIN7_ROWS_REQUIRED` |
| 18 | Win7POS fictitious receipt through Epson TM-T60, full text, accents, totals and automatic cutter. | `PASS_PHYSICAL_2026-07-17` | `YES` | `NON_BLOCKING_OWNER_AUTHORIZED` | `PARTIAL_ONLY_CASH_CARD_REPRINT_FAILURE_AND_DRAWER_MATRIX_REMAIN` |

Completion evidence must come from the corresponding authenticated staging or
physical Win7/hardware execution. Static checks, synthetic fixtures, lifecycle
harnesses, local packaging and CI evidence do not change these statuses to PASS.
Row 17 is backed by operator-observed paper and a retained photograph. Row 18
is backed by the operator-confirmed Win7POS fictitious receipt with complete
content and automatic cut. Row 15 is backed by the operator's explicit
confirmation that the single previously submitted pin-2 pulse opened the drawer
exactly once. No second pulse was sent. These rows do not close cash/card sale,
reprint, printer-failure, authenticated settings, transactional drawer behavior
or physical Windows 7 validation.

The retained production-code/spooler evidence for row 15 records the one command,
normal/empty pre/post queue and absence of a QA database. The later operator
observation closes only that manual pin-2 row; no automatic retry is permitted.
