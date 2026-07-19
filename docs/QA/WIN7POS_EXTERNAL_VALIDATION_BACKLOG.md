# Win7POS external validation backlog

## Status and scope

- Backlog status: `OPEN`.
- External validation status: `EPSON_RECEIPT_SURFACE_ADDENDUM_PASS_WINDOWS7_OPEN`.
- Software merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production/hardware certification: `OPEN` and `NOT_YET_CERTIFIED`.

Rows 15 and 17–25 are the only rows declared physically PASS. Every other open
item is non-blocking for the owner-authorized software merge and remains
mandatory before production or full hardware certification.

| # | External validation item | Status | PASS declared | Software merge gate | Production/hardware certification |
| ---: | --- | --- | --- | --- | --- |
| 1 | Staging catalog full iniziale. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 2 | Staging incremental create/update/price/stock/tombstone. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 3 | Resume e network recovery reali. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 4 | Vendite cash/card/mixed. | `PARTIAL_PHYSICAL_CASH_CARD_PASS_MIXED_PENDING` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `MIXED_REQUIRED_BEFORE_FULL_CERTIFICATION` |
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
| 19 | Epson transactional cash receipt and cutter; drawer opens once. | `PASS_PHYSICAL_SINGLE_OPEN_2026-07-18` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 20 | Epson card-only receipt; drawer remains closed. | `PASS_PHYSICAL_NO_DRAWER_2026-07-18` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 21 | Persisted-sale receipt reprint; no new sale and drawer remains closed. | `PASS_PHYSICAL_NO_DRAWER_2026-07-18` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 22 | Paused queue commits before print failure; resumed reprint has no duplicate and no drawer opening. | `PASS_COMMIT_BEFORE_PRINT_NO_DUPLICATE_2026-07-18` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 23 | Direct fiscal QA output at 32 and 42 columns. | `PASS_PHYSICAL_NO_DRAWER_2026-07-19` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 24 | Exact receipt original/reprint request produces identical physical output. | `PASS_PHYSICAL_IDENTICAL_NO_DRAWER_2026-07-19` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |
| 25 | Dedicated daily-close output at 32 and 42 columns. | `PASS_PHYSICAL_NO_DRAWER_2026-07-19` | `YES` | `CLOSED_FOR_PR7_MERGE` | `PASS_FOR_TESTED_QA_SCENARIO` |

Completion evidence must come from the corresponding authenticated staging or
physical Win7/hardware execution. Static checks, synthetic fixtures, lifecycle
harnesses, local packaging and CI evidence do not change these statuses to PASS.
Row 17 is backed by operator-observed paper and a retained photograph. Row 18
is backed by the operator-confirmed Win7POS fictitious receipt with complete
content and automatic cut. Row 15 is backed by the operator's explicit
confirmation that the single previously submitted pin-2 pulse opened the drawer
exactly once. No second pulse was sent. Rows 15, 17 and 18 do not by themselves
close cash/card sale, reprint, printer-failure, authenticated settings,
transactional drawer behavior or physical Windows 7 validation. Rows 19–22
close the tested QA cash, card, reprint and paused/resumed-queue matrix with
operator-confirmed paper and drawer behavior. Rows 23–25 close the PR #7
receipt-surface/daily-close addendum with one no-database, no-drawer six-job
sequence and operator-confirmed paper. They do not close mixed payment,
disconnected drawer, authenticated settings or physical Windows 7 validation.

The retained production-code/spooler evidence for row 15 records the one command,
normal/empty pre/post queue and absence of a QA database. The later operator
observation closes only that manual pin-2 row; no automatic retry is permitted.
