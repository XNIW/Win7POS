# Catalog apply performance — 19,763 rows

Date: 2026-07-23

Branch: `agent/final-catalog-apply-performance`

Runtime under test: `net48`, x86, Windows, SQLite DELETE journal with `synchronous=FULL`

## Change

The commit fence, validation, price-history semantics, and authoritative
reconciliation remain unchanged. A full refresh now adopts the already
validated authoritative stage into the post-repair epoch and verifies the
exact page marker before applying it. It no longer deletes and reinserts the
same durable stage rows a second time during apply. Missing or ambiguous stage
scope/page evidence fails closed inside the transaction.

The complete downloaded response chain is replayed a page at a time into one
authoritative-stage transaction before the repair epoch is changed. The
validated full refresh is then promoted in one apply transaction. A staging or
apply failure rolls back the whole run, diagnostics and the catalog revision
are published only after the physical commit, and delta pages retain their
independent transaction boundary. The durable response provider still loads
one page at a time, so neither transaction requires a complete in-memory
catalog materialization.

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
- p50: 2,809.491 ms
- p90: 2,957.192 ms
- p95: 2,959.802 ms
- Maximum: 2,986.320 ms
- Peak working set: 57,319,424 bytes
- Peak private bytes: 40,742,912 bytes
- Dispatcher median maximum delay: 12.886 ms
- Dispatcher maximum delay: 24.475 ms
- Maximum Gen2 collections: 1
- Authoritative-stage transactions: 1 per sample
- Full-refresh apply transactions: 1 per sample
- Context SQL commands: 103 per sample
- Relink identity staging commands: 20 per sample
- Remote-price SQL commands: 140 per sample
- Remote-price SQL statements: 358 per sample

Measured elapsed times in milliseconds:

`2986.320, 2839.148, 2887.972, 2806.450, 2855.924, 2957.192, 2950.150, 2796.912, 2749.696, 2669.628, 2693.933, 2792.431, 2746.406, 2553.900, 2797.595, 2959.802, 2880.606, 2898.200, 2810.900, 2808.082`

These measurements executed the rebuilt net48/x86 benchmark apphost directly.
The exact-head GitHub workflow remains authoritative for the remote 15-second
release gate.

## Bounded-path checks

- Delta 10: 54.854 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 100: 73.154 ms; 8 context commands; 1 relink staging command;
  7 price commands / 9 statements
- Delta 1,000: 153.235 ms; 8 context commands; 1 relink staging command;
  7 price commands / 18 statements
- No-change: 20/20 exact; zero context/price writes; p95 0.037 ms
- 100,000 net48/x86: `Verified`; products/prices 100,000; pending 0;
  authoritative staging rows after cleanup 0; 100 relink staging commands;
  1 stage transaction; 1 apply transaction; 503 context commands;
  700 price commands / 1,800 statements; Gen2 collections 11;
  peak working set 58,228,736 bytes; peak private bytes 41,324,544 bytes;
  dispatcher maximum 21.812 ms

## Regression evidence

- Core/Data: 618 passed, 0 failed, 0 skipped
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

Dedicated rollback regressions also prove that a failure in the authoritative
stage or on any full-refresh apply page leaves no partial run committed.
