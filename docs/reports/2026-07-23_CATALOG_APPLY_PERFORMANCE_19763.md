# Catalog apply performance — 19,763 rows

Date: 2026-07-23

Branch: `agent/final-catalog-apply-performance`

Runtime under test: `net48`, x86, Windows, SQLite DELETE journal with `synchronous=FULL`

## Change

The page transaction, commit fence, validation, price-history semantics, and
authoritative reconciliation remain unchanged. A full refresh now adopts the
already validated authoritative stage into the post-repair epoch and verifies
the exact page marker before applying it. It no longer deletes and reinserts
the same durable stage rows a second time during apply. Missing or ambiguous
stage scope/page evidence fails closed inside the page transaction.

Page-scoped identities,
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
- p50: 3,730.046 ms
- p90: 3,865.859 ms
- p95: 3,912.935 ms
- Maximum: 3,916.935 ms
- Peak working set: 57,651,200 bytes
- Peak private bytes: 41,181,184 bytes
- Dispatcher median maximum delay: 19.592 ms
- Dispatcher maximum delay: 21.933 ms
- Maximum Gen2 collections: 1
- Context SQL commands: 103 per sample
- Relink identity staging commands: 20 per sample
- Remote-price SQL commands: 140 per sample
- Remote-price SQL statements: 358 per sample

Measured elapsed times in milliseconds:

`3565.088, 3853.925, 3728.876, 3287.201, 3695.743, 3776.209, 3714.041, 3790.225, 3767.159, 3916.935, 3636.871, 3737.191, 3809.263, 3731.216, 3559.237, 3620.461, 3912.935, 3714.607, 3865.859, 3705.587`

These measurements executed the rebuilt net48/x86 benchmark apphost directly.
The exact-head GitHub workflow remains authoritative for the remote 15-second
release gate.

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
  Gen2 collections 8; peak working set 57,958,400 bytes;
  peak private bytes 40,656,896 bytes; dispatcher maximum 22.231 ms

## Regression evidence

- Core/Data: 616 passed, 0 failed, 0 skipped
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
