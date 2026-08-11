# Win7POS external validation backlog

## Status and scope

- Backlog status: `TERMINAL_CLOSED_OWNER_ACCEPTED_DEFERRED`.
- External validation status: `DONE_OWNER_ACCEPTED_DEFERRED` for the exact
  final closeout candidate; the historical PASS observations below remain
  evidence for their original builds only.
- Software merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production/hardware certification: `NOT_QUALIFIED`.

Rows 15 and 17–25 are the only rows with historical physical PASS evidence.
No canonical row was re-executed on the exact final candidate because the
Windows 7 target and required peripherals were unavailable. The owner-authorized
terminal decision for every row is therefore `DONE_OWNER_ACCEPTED_DEFERRED`;
each row remains mandatory before production or full hardware certification.

| # | External validation item | Status | PASS declared | Software merge gate | Production/hardware certification |
| ---: | --- | --- | --- | --- | --- |
| 1 | Staging catalog full iniziale. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 2 | Staging incremental create/update/price/stock/tombstone. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 3 | Resume e network recovery reali. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 4 | Vendite cash/card/mixed. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 5 | Offline/reconnect e idempotenza server. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 6 | Refund e void reali. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 7 | Riconciliazione giornaliera. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 8 | BusinessDate/mezzanotte. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 9 | Windows 7 SP1 fisico. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 10 | Installer smoke Win7. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 11 | Dual-monitor Windows Extend. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 12 | Customer display fisico. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 13 | Scanner. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 14 | Xprinter. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 15 | Cash drawer manual pin-2 pulse. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical single-open PASS retained |
| 16 | Profili DPI e IT/EN/ES/ZH runtime. | `DONE_OWNER_ACCEPTED_DEFERRED` | `NO` | `TERMINAL_NON_BLOCKING` | `REQUIRED_BEFORE_CERTIFICATION` |
| 17 | Epson TM-T60 APD/Windows/Notepad 80 mm print, accents, feed and cutter. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 18 | Win7POS fictitious receipt through Epson TM-T60, full text, accents, totals and automatic cutter. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 19 | Epson transactional cash receipt and cutter; drawer opens once. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 20 | Epson card-only receipt; drawer remains closed. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 21 | Persisted-sale receipt reprint; no new sale and drawer remains closed. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 22 | Paused queue commits before print failure; resumed reprint has no duplicate and no drawer opening. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 23 | Direct fiscal QA output at 32 and 42 columns. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 24 | Exact receipt original/reprint request produces identical physical output. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |
| 25 | Dedicated daily-close output at 32 and 42 columns. | `DONE_OWNER_ACCEPTED_DEFERRED` | `YES` | `TERMINAL_NON_BLOCKING` | `REQUIRED_ON_FINAL_CANDIDATE`; historical PASS retained |

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

## 2026-08-09 acceptance execution

This section is retained as historical evidence and is superseded for current
closeout status by the terminal disposition above.

- Acceptance code SHA:
  `b6a92b0d8f6d26ceee4f78a29f39d5862e8ef3ef`.
- Classification: `BLOCKED_EXTERNAL`.
- Before and after: `10/25` historical physical PASS rows, `15` open/partial
  rows; newly closed rows: `0`.
- No physical Windows 7 device, scanner, Xprinter, drawer, customer display, or
  dual-monitor station was available. No drawer impulse was sent.
- No isolated authenticated QA shop/account was supplied. Only public HTTPS
  reachability was observed from the Windows 11 build host; no authenticated
  login, catalog, sale, import, refund/void, or idempotency scenario ran.
- The unsigned branch installer was built and statically validated, but no
  Win7 install/upgrade/uninstall or signed-artifact smoke was performed.
- Automated fixtures, 95 retained UI screenshots (47 in the canonical PASS
  run), synthetic lifecycle/hardware-manager smoke, CI, and release packaging
  do not promote any external row.
- Detailed report:
  `docs/QA/WIN7POS_PHYSICAL_WIN7_HARDWARE_ACCEPTANCE_2026-08-09.md`.
