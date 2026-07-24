# Catalog apply performance — 19,763 rows

Date: 2026-07-23

Branch: `agent/final-catalog-apply-performance`

Runtime under test: `net48`, x86, Windows, SQLite DELETE journal with `synchronous=FULL`

## Change

The page transaction, commit fence, validation, price-history semantics, and
authoritative reconciliation remain unchanged. Page-scoped identities,
set-based product rows, and remote price rows are now encoded as bounded
parameterized JSON payloads and expanded with SQLite `json_each`. Each command
contains at most 1,000 rows.

This removes parameter-heavy staging loops without using unsafe pragmas,
reducing the full-run catalog context from 598 to 103 SQL commands and the
remote-price apply from 338 commands / 358 statements to 140 commands / 180
statements. The original pre-optimization price path recorded 158,144
statements.

## Required net48/x86 measurement

The database was recreated deterministically for every sample. Two warm-up
samples were excluded, followed by 20 measured full-refresh samples on the same
host with 19,763 products, 19,763 prices, and page size 1,000.

- Exactness: 20/20 `Verified`; products 19,763; prices 19,763; pending 0
- p50: 4,653.104 ms
- p90: 4,801.169 ms
- p95: 4,825.837 ms
- Maximum: 5,056.432 ms
- Peak working set: 65,662,976 bytes
- Peak private bytes: 49,418,240 bytes
- Dispatcher median maximum delay: 20.610 ms
- Dispatcher maximum delay: 25.973 ms
- Context SQL commands: 103 per sample
- Remote-price SQL commands: 140 per sample
- Remote-price SQL statements: 180 per sample

Measured elapsed times in milliseconds:

`4762.151, 4661.430, 4738.807, 4554.843, 4617.445, 4493.253, 4801.169, 4507.665, 4451.707, 4825.837, 4490.317, 4522.346, 4771.798, 4793.527, 4644.778, 4721.363, 4639.882, 5056.432, 4688.884, 4564.658`

## Bounded-path checks

- Delta 10: 47.270 ms; 8 context commands; 7 price commands / 9 statements
- Delta 100: 58.165 ms; 8 context commands; 7 price commands / 9 statements
- Delta 1,000: 104.461 ms; 8 context commands; 7 price commands / 9 statements
- No-change: 20/20 exact; zero context/price writes; p95 0.005 ms
- 100,000 net48/x86: `Verified`; products/prices 100,000; pending 0;
  authoritative staging rows after cleanup 0; peak working set 63,508,480
  bytes; peak private bytes 46,755,840 bytes; dispatcher maximum 27.057 ms

## Regression evidence

- Core/Data: 613 passed, 0 failed, 0 skipped
- Canonical gates: 44/44
- WPF Release net48/x86: 0 warnings, 0 errors
- CLI self-test: PASS
- x86 bounded logging smoke: PASS
- 100,000-row WPF paging dispatcher smoke: PASS
- Gitleaks 8.30.1 working tree and full history: 0 findings

The thousand-row regression assertions now require one page to use no more
than 15 catalog-context commands and exactly 7 remote-price commands / 9
statements. The prior staging implementation fails these bounds.
