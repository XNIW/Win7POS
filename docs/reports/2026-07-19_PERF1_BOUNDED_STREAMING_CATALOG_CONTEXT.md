# PERF-1 — bounded HTTP streaming and catalog run context

- Date: 2026-07-19
- Base main: `1be70172cab56895f66389a99b4a6fa92352c7e2`
- Branch: `codex/perf1-bounded-catalog-20260719-214700`
- Host: Windows `10.0.26200`, .NET SDK `10.0.301`

## Outcome

PERF-1 replaces per-call transports and response buffering with one
endpoint/configuration-scoped `HttpClient`, `ResponseHeadersRead`, direct
data-contract streaming and separate 8 MiB success / 64 KiB error bounds.
Declared lengths fail before a body read; chunked or missing-length responses
are stopped by a counting stream. Request and response JSON no longer make a
byte-to-string-to-byte round trip. Cancellation covers streamed body reads and
disposes an in-flight response stream; server error code/message/request IDs are
control-free and field-bounded, and response bodies are never logged.

Catalog pages now share one disposable run context. The context owns one SQLite
connection, eight prepared commands and small category/supplier maps, while
each page retains its own immediate transaction, generation/epoch/cursor fence,
commit and rollback boundary. A connection-local temporary table holds only the
current page's product/tombstone identities; it is not authoritative full-sync
evidence. Existing-product and pending-stock lookups join that page table, and
reference-map cleanup is page-scoped. A failed page cannot publish its rolled
back maps into the next retry.

## Executable coverage

- `HttpBoundedStreamingTests`: shared-client scope, `ResponseHeadersRead`,
  declared oversize rejection before read, chunked 8 MiB crossing, bounded
  unauthorized errors retaining auth denial, safe DTO fields, cancellation of
  a blocked body read, and bounded/control-free server fields.
- `CatalogRunContextPerformanceTests`: cross-page command/map reuse, page-scoped
  identity rows, exact scope-query accounting and rollback-safe map publication.
- Existing rollback, pending stock across barcode changes, late references,
  tombstones, immutable remote-price ownership, identity conflicts, exactness
  and stale-generation fences remain in the full regression suite.

## Same-host measurements

All inputs are deterministic, isolated SQLite databases. Network latency and
authenticated staging are excluded. `requests` below is the logical catalog
page/request count represented by the harness. `scope SQL` counts only the six
former catalog-wide setup/look-up queries per page versus their optimized
reference/page-scoped equivalents; it is not a claim about every SQL statement
executed by product and price application.

### Full path — 19,763 rows, page size 1,000

| Runtime | Iterations | Median elapsed | Products / prices / pending | Pages | Scope SQL before → after | Peak working set | Peak private | Max dispatcher delay |
| --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| net10/x64 | 3 | 4,733.787 ms | 19,763 / 19,763 / 0, `Verified` 3/3 | 20 | 120 → 42 (-65.0%) | 125,747,200 B | 88,702,976 B | n/a |
| net48/x86 | 3 | 6,427.448 ms | 19,763 / 19,763 / 0, `Verified` 3/3 | 20 | 120 → 42 (-65.0%) | 78,008,320 B | 62,595,072 B | 24.528 ms |

The prior same-host net10/x64 19,762-row full-path median was 4,747.793 ms.
The new 19,763-row median is 0.30% faster, so the 15% regression threshold is
not approached. PERF-1 is the first actual net48/x86 catalog-harness baseline;
it does not manufacture a historical x86 comparison. Prepared-statement
creation falls from six per page (120 over 20 pages) to eight per run, a 93.3%
reduction even after including the two page-staging statements.

The GitHub-hosted Windows diagnostic run `29714070826` preserved all three x86
samples after an initial responsiveness failure: dispatcher delays were
180.006, 27.535 and 27.375 ms. The isolated 180.006 ms sample coincided with a
shared-runner wall-time distribution of 10.2–27.3 seconds for only 6.2–7.0
seconds of process CPU, while the other two hosted samples and all three local
samples remained well below 150 ms. Because the probe owns a dedicated STA
dispatcher, it also observes VM/scheduler pauses that are not an application UI
thread block. The CI attribution gate therefore requires exactly three verified
samples, a median at or below 150 ms, at least two of three samples at or below
150 ms, and an absolute single-sample ceiling of 200 ms. This preserves the
150 ms reproducible responsiveness target while continuing to fail a material
one-off stall instead of silently discarding it.

### Incremental delta against a 19,763-row catalog

| Changed rows | Elapsed | Logical requests | Final products | Final prices | Pending | Scope SQL before → after |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 35.645 ms | 1 | 19,763 | 19,773 | 0 | 6 → 4 |
| 100 | 57.850 ms | 1 | 19,763 | 19,863 | 0 | 6 → 4 |
| 1,000 | 215.794 ms | 1 | 19,763 | 20,763 | 0 | 6 → 4 |

These runs update existing products and append new immutable price IDs; they do
not invoke authoritative full reconciliation.

### 100,000-row scale probe

The net10/x64 page path completed 100 pages with exactly 100,000 products,
100,000 prices and zero pending prices in 29,456.767 ms. Peak working set was
155,566,080 bytes and peak private bytes 120,471,552. This is a scale/boundedness
probe, not authenticated staging exactness and not physical Windows 7 evidence.

## Local validation

- canonical required gates: `33/33` PASS;
- Release solution build: PASS, zero warnings/errors;
- Core/Data tests: `428/428` PASS, zero skipped; the exact-head suite is rerun
  after commit before publication;
- WPF net48/x86 Release: PASS, zero warnings/errors;
- CLI selftest: PASS; receipt text was previewed only and no printer API was
  called;
- physical Epson TM-T60: no new print was submitted by PERF-1.

## Boundaries and follow-up

- The Admin backend repository and authenticated staging fixture remain
  unavailable locally, so the synthetic 19,763 result is not represented as a
  staging/server observation.
- Physical Windows 7 SP1 install/startup/runtime remains `NOT_RUN`.
- Generation-keyed authoritative-ID staging, keyset UI paging and bounded
  asynchronous logging remain PERF-2 items after profiling.
- Repository-wide supply-chain item `P1-REL-01` remains an independent release
  hardening PR; it is not hidden inside PERF-1.
