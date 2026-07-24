# Catalog apply performance — 19,763 rows

Date: 2026-07-23

Branch: `agent/final-catalog-apply-performance`

Runtime under test: `net48`, x86, Windows, SQLite DELETE journal with `synchronous=FULL`

## Change

The page transaction, commit fence, validation, price-history semantics, and
authoritative reconciliation remain unchanged. Page-scoped identities,
set-based product rows, remote price rows, and reference relink identities are
now encoded as bounded parameterized JSON payloads and expanded with SQLite
`json_each`. Each command contains at most 1,000 rows.

This removes parameter-heavy staging loops without using unsafe pragmas,
reducing the full-run catalog context from 598 to 103 SQL commands and the
remote-price apply from 338 commands / 358 statements to 140 commands / 180
statements. The 19,763 reference identities are staged in 20 commands rather
than 19,763 individual executions. The original pre-optimization price path
recorded 158,144 commands and 177,907 statements: reductions of 99.911% and
99.899%, respectively.

## Required net48/x86 measurement

The database was recreated deterministically for every sample on one stable
temporary session path. This preserves fresh-database isolation without
introducing per-sample path churn. Two warm-up samples were excluded, followed
by 20 measured full-refresh samples on the same host with 19,763 products,
19,763 prices, and page size 1,000.

- Exactness: 20/20 `Verified`; products 19,763; prices 19,763; pending 0
- p50: 3,544.574 ms
- p90: 3,659.530 ms
- p95: 3,715.443 ms
- Maximum: 3,867.261 ms
- Peak working set: 65,212,416 bytes
- Peak private bytes: 48,660,480 bytes
- Dispatcher median maximum delay: 23.373 ms
- Dispatcher maximum delay: 28.436 ms
- Context SQL commands: 103 per sample
- Relink identity staging commands: 20 per sample
- Remote-price SQL commands: 140 per sample
- Remote-price SQL statements: 180 per sample

Measured elapsed times in milliseconds:

`3867.261, 3715.443, 3569.928, 3570.057, 3587.062, 3529.233, 3570.629, 3384.410, 3617.061, 3497.203, 3524.778, 3500.145, 3647.666, 3440.781, 3510.147, 3357.962, 3494.468, 3423.726, 3659.530, 3559.914`

Local Windows Application Control blocks the newly rebuilt unsigned benchmark
apphost. The stable-path timing rerun therefore loaded the same net48/x86
assemblies in the signed 32-bit Windows PowerShell host. The memory figures
above remain the direct-apphost figures from the immediately preceding run of
the same production implementation; the exact-head GitHub workflow executes
the apphost directly and is authoritative for both time and memory gates.

## Bounded-path checks

- Delta 10: 55.486 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 100: 59.429 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 1,000: 145.429 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- No-change: 20/20 exact; zero context/price writes; p95 0.005 ms
- 100,000 net48/x86: `Verified`; products/prices 100,000; pending 0;
  authoritative staging rows after cleanup 0; 100 relink staging commands;
  peak working set 63,201,280 bytes; peak private bytes 46,108,672 bytes;
  dispatcher maximum 25.614 ms

## Regression evidence

- Core/Data: 614 passed, 0 failed, 0 skipped
- Canonical gates: 44/44
- WPF Release net48/x86: 0 warnings, 0 errors
- CLI self-test: PASS
- x86 bounded logging smoke: PASS
- 100,000-row WPF paging dispatcher smoke: PASS
- Gitleaks 8.30.1 working tree and full history: 0 findings

The thousand-row regression assertions now require one page to use no more
than 15 catalog-context commands and exactly 7 remote-price commands / 9
statements. A dedicated regression also requires 1,000 relink identities to use
one bounded JSON staging command. The prior staging implementations fail these
bounds.
