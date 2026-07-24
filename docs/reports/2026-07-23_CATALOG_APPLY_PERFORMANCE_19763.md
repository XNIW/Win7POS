# Catalog apply performance — 19,763 rows

Date: 2026-07-23

Branch: `agent/final-catalog-apply-performance`

Runtime under test: `net48`, x86, Windows, SQLite DELETE journal with `synchronous=FULL`

## Change

The page transaction, commit fence, validation, price-history semantics, and
authoritative reconciliation remain unchanged. Page-scoped identities,
set-based product rows, remote price rows, and reference relink identities are
now encoded as bounded parameterized JSON payloads and expanded with SQLite
`json_each`. Product and authoritative payloads are split below the net48
large-object threshold; durable authoritative chunks contain at most 200 rows
and the wide product/price chunks at most 100 rows. Multiple small chunks are
bound to one prepared command so each staging phase still has one SQLite
round-trip per page.

This removes parameter-heavy staging loops without using unsafe pragmas,
reducing the full-run catalog context from 598 to 103 SQL commands. The 19,763
reference identities are staged in 20 commands rather than 19,763 individual
executions. The original pre-optimization price path recorded 158,144 commands
and 177,907 statements; the bounded set-based path records 140 commands and 358
statements, reductions of 99.911% and 99.799%, respectively. Keeping individual
JSON buffers below the large-object threshold reduces measured Gen2 collections
from about 20 per sample to at most 1.

## Required net48/x86 measurement

The database was recreated deterministically for every sample on one stable
temporary session path. This preserves fresh-database isolation without
introducing per-sample path churn. Two warm-up samples were excluded, followed
by 20 measured full-refresh samples on the same host with 19,763 products,
19,763 prices, and page size 1,000.

- Exactness: 20/20 `Verified`; products 19,763; prices 19,763; pending 0
- p50: 3,058.202 ms
- p90: 3,081.521 ms
- p95: 3,114.069 ms
- Maximum: 3,118.485 ms
- Signed-host peak working set: 106,496,000 bytes
- Signed-host peak private bytes: 82,554,880 bytes
- Dispatcher median maximum delay: 24.357 ms
- Dispatcher maximum delay: 28.865 ms
- Maximum Gen2 collections: 1
- Context SQL commands: 103 per sample
- Relink identity staging commands: 20 per sample
- Remote-price SQL commands: 140 per sample
- Remote-price SQL statements: 358 per sample

Measured elapsed times in milliseconds:

`3118.485, 3070.855, 3051.300, 3050.920, 3051.092, 3114.069, 3081.521, 3020.353, 3030.451, 3058.312, 3070.163, 3077.740, 3069.004, 3079.318, 3062.730, 3050.338, 3050.652, 3040.410, 3052.031, 3058.091`

Local Windows Application Control blocks the newly rebuilt unsigned benchmark
apphost. The stable-path timing rerun therefore loaded the same net48/x86
assemblies in the signed 32-bit Windows PowerShell host. Its absolute process
memory includes that host and is not apphost-comparable; the exact-head GitHub
workflow executes the apphost directly and is authoritative for both time and
memory gates.

## Bounded-path checks

- Delta 10: 56.888 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 100: 58.506 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 1,000: 91.676 ms; 8 context commands; 1 relink staging command;
  7 price commands / 18 statements
- No-change: 20/20 exact; zero context/price writes; p95 0.005 ms
- 100,000 net48/x86: `Verified`; products/prices 100,000; pending 0;
  authoritative staging rows after cleanup 0; 100 relink staging commands;
  503 context commands; 700 price commands / 1,800 statements;
  maximum Gen2 collections 2; signed-host peak working set 107,847,680 bytes;
  signed-host peak private bytes 82,968,576 bytes; dispatcher maximum 28.836 ms

## Regression evidence

- Core/Data: 614 passed, 0 failed, 0 skipped
- Canonical gates: 44/44
- WPF Release net48/x86: 0 warnings, 0 errors
- CLI self-test: PASS
- x86 bounded logging smoke: PASS
- 100,000-row WPF paging dispatcher smoke: PASS
- Gitleaks 8.30.1 working tree and full history: 0 findings

The thousand-row regression assertions now require one page to use no more
than 15 catalog-context commands and exactly 7 remote-price commands / 18
statements. A dedicated regression also requires 1,000 relink identities to use
one bounded JSON staging command. The prior per-row, large-object, and
multi-round-trip staging implementations fail these bounds.
