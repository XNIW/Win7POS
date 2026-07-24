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
large-object threshold; no payload command contains more than 500 rows and the
wide product/price payloads contain at most 100 rows.

This removes parameter-heavy staging loops without using unsafe pragmas,
reducing the full-run catalog context from 598 to 301 SQL commands. The 19,763
reference identities are staged in 20 commands rather than 19,763 individual
executions. The original pre-optimization price path recorded 158,144 commands
and 177,907 statements; the bounded set-based path records 318 commands and 536
statements, reductions of 99.799% and 99.699%, respectively. Keeping individual
JSON buffers below the large-object threshold reduces measured Gen2 collections
from about 20 per sample to at most 1.

## Required net48/x86 measurement

The database was recreated deterministically for every sample on one stable
temporary session path. This preserves fresh-database isolation without
introducing per-sample path churn. Two warm-up samples were excluded, followed
by 20 measured full-refresh samples on the same host with 19,763 products,
19,763 prices, and page size 1,000.

- Exactness: 20/20 `Verified`; products 19,763; prices 19,763; pending 0
- p50: 3,276.443 ms
- p90: 3,498.706 ms
- p95: 3,704.601 ms
- Maximum: 3,890.200 ms
- Signed-host peak working set: 105,492,480 bytes
- Signed-host peak private bytes: 81,666,048 bytes
- Dispatcher median maximum delay: 20.714 ms
- Dispatcher maximum delay: 28.500 ms
- Maximum Gen2 collections: 1
- Context SQL commands: 301 per sample
- Relink identity staging commands: 20 per sample
- Remote-price SQL commands: 318 per sample
- Remote-price SQL statements: 536 per sample

Measured elapsed times in milliseconds:

`3890.200, 3704.601, 3498.706, 3318.174, 3464.923, 3447.703, 3489.500, 3325.672, 3311.673, 3241.212, 3086.339, 3119.049, 3079.431, 3048.256, 3181.185, 3442.770, 3231.774, 3135.002, 3096.341, 3094.082`

Local Windows Application Control blocks the newly rebuilt unsigned benchmark
apphost. The stable-path timing rerun therefore loaded the same net48/x86
assemblies in the signed 32-bit Windows PowerShell host. Its absolute process
memory includes that host and is not apphost-comparable; the exact-head GitHub
workflow executes the apphost directly and is authoritative for both time and
memory gates.

## Bounded-path checks

- Delta 10: 44.925 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 100: 45.229 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 1,000: 87.474 ms; 18 context commands; 1 relink staging command;
  16 price commands / 27 statements
- No-change: 20/20 exact; zero context/price writes; p95 0.005 ms
- 100,000 net48/x86: `Verified`; products/prices 100,000; pending 0;
  authoritative staging rows after cleanup 0; 100 relink staging commands;
  1,503 context commands; 1,600 price commands / 2,700 statements;
  maximum Gen2 collections 2; signed-host peak working set 107,491,328 bytes;
  signed-host peak private bytes 82,399,232 bytes; dispatcher maximum 26.632 ms

## Regression evidence

- Core/Data: 614 passed, 0 failed, 0 skipped
- Canonical gates: 44/44
- WPF Release net48/x86: 0 warnings, 0 errors
- CLI self-test: PASS
- x86 bounded logging smoke: PASS
- 100,000-row WPF paging dispatcher smoke: PASS
- Gitleaks 8.30.1 working tree and full history: 0 findings

The thousand-row regression assertions now require one page to use no more
than 25 catalog-context commands and exactly 16 remote-price commands / 27
statements. A dedicated regression also requires 1,000 relink identities to use
one bounded JSON staging command. The prior per-row and large-object staging
implementations fail these bounds.
