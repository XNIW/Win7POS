# ASUS-W7POS-014 — Catalog SQLite batch apply

- Date: 2026-07-14
- Branch: `integration/asus-catalog-ui-runtime-20260714`
- Closure software commit: `bb4178b506afe5ebc86313f010e2271ca8075f84`

## Outcome

The catalog apply path uses one SQLite connection and one transaction per page or controlled batch, reuses prepared product/meta statements, orders references before products, and keeps product/meta, remote-reference mapping, prices, pending-price replay, and tombstones inside the same atomic boundary. SQLite writes remain single-writer.

The controlled local comparison exceeds the indicative 2× target. The final synthetic 19,762-row run also covers page apply, immutable price ownership, full reconciliation, and exactness verification and finishes `Verified` in all three iterations. These numbers are not an authenticated staging full refresh and are not proof that staging contains exactly 19,762 products.

## Task ledger

| Task | State | Commit | Evidence | Notes |
|---|---|---|---|---|
| ASUS-W7POS-014.1 batch apply implementation | DONE | `bb4178b` | One connection/transaction per page; prepared statements; rollback and retry tests | Integrated with `PosCatalogPullService`; service integration complete |
| ASUS-W7POS-014.2 controlled apply benchmark | DONE | `bb4178b` | `catalog-performance-final-closure-2000.trx` | Closure code; legacy and batch inputs cross the same price-ownership invariants |
| ASUS-W7POS-014.3 19,762 apply-only scale probes | DONE | `bb4178b` | `catalog-performance-final-19762.trx`, `catalog-performance-final-19762-paged.trx` | Historical pre-closure synthetic probes; not staging exactness evidence |
| ASUS-W7POS-014.4 apply + reconcile + verify benchmark | DONE | `bb4178b` | `catalog-performance-final-closure-19762-paged-full.trx` | Closure code; 19,762 products/prices; 20 pages; three `Verified` iterations |
| ASUS-W7POS-014.5 authenticated staging full refresh | BLOCKED_EXTERNAL | — | staging login is unavailable without credentials | Expected product count from the brief is 19,762; it was not observed by this benchmark |

## Implementation invariants

`RemoteCatalogBatchRepository.ApplyAsync(RemoteCatalogBatch, CancellationToken)` applies categories, suppliers, products/meta and their persistent remote-reference mapping, pending-price replay, tombstones, and prices on one connection and transaction. A page commits only when all rows are coherent; otherwise it rolls back.

The integrated path also preserves:

- unresolved-outbox stock across remote barcode changes;
- canonical remote identities and duplicate suppression;
- cross-page category/supplier reference relinking;
- incremental reference relinking limited to page-affected products through indexed temporary target tables;
- pending remote-price queue/replay and remote price-ID idempotency;
- immutable remote-price ownership with append-only quarantine for ambiguous legacy evidence during authoritative repair;
- non-destructive tombstones;
- safe cancellation at batch boundaries;
- fail-closed skipped-row accounting consumed by the pull service.

## Controlled local benchmark — 2,000 products

Environment: Windows `10.0.26200`, .NET SDK `10.0.301`, fresh isolated SQLite database for each iteration. Each timed run receives 40 categories, 40 suppliers, 2,000 products, and 2,000 prices. Input construction and schema initialization are outside the timed region.

Evidence: `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-closure-2000.trx`.

| Mode | Iteration | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---|---:|---:|---:|---:|---:|---:|
| legacy per-row | 1 | 16,798.825 | 119.06 | 8,406.250 | 87,535,616 | 1,572,864 |
| legacy per-row | 2 | 17,324.898 | 115.44 | 7,968.750 | 91,426,816 | 1,572,864 |
| legacy per-row | 3 | 18,043.834 | 110.84 | 8,109.375 | 89,567,232 | 1,572,864 |
| page batch | 1 | 308.594 | 6,481.00 | 296.875 | 94,367,744 | 1,847,296 |
| page batch | 2 | 319.620 | 6,257.42 | 437.500 | 104,366,080 | 1,847,296 |
| page batch | 3 | 299.818 | 6,670.72 | 312.500 | 104,878,080 | 1,847,296 |

Median wall time improves from 17,324.898 ms to 308.594 ms (`56.14×`), and median throughput from 115.44 to 6,481.00 product rows/s (`56.14×`). Median CPU improves from 8,109.375 ms to 312.500 ms (`25.95×`). Median working set rises about 16.52%.

The batch database is 17.45% larger because it additionally persists the authoritative product-reference map. Both modes also persist immutable remote-price ownership. Therefore database-size equality is not claimed; both modes still finish with exactly 2,000 products, 2,000 price rows, and zero pending prices.

## Synthetic 19,762-row apply-only probes

These historical scale probes use deterministic generated data and predate the final immutable-price-ownership hardening. They do not include HTTP, parsing, full-refresh reconciliation, exactness evaluation, staging latency, or UI responsiveness; the final full-path measurement is in the next section.

### Single batch

Evidence: `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-19762.trx`.

| Iteration | Products | Prices | Pending | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 19,762 | 19,762 | 0 | 1,432.182 | 13,798.53 | 1,593.750 | 105,787,392 | 12,820,480 |
| 2 | 19,762 | 19,762 | 0 | 963.944 | 20,501.18 | 1,234.375 | 125,059,072 | 12,820,480 |
| 3 | 19,762 | 19,762 | 0 | 933.313 | 21,174.03 | 1,125.000 | 133,881,856 | 12,820,480 |

Median: 963.944 ms and 20,501.18 product rows/s.

### Paged batch, 1,000 products per page

Evidence: `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-19762-paged.trx`.

| Iteration | Page size | Products | Prices | Pending | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 1,000 | 19,762 | 19,762 | 0 | 2,837.760 | 6,963.94 | 3,296.875 | 109,408,256 | 12,820,480 |
| 2 | 1,000 | 19,762 | 19,762 | 0 | 2,286.283 | 8,643.72 | 2,250.000 | 124,866,560 | 12,820,480 |
| 3 | 1,000 | 19,762 | 19,762 | 0 | 2,352.709 | 8,399.68 | 2,484.375 | 132,075,520 | 12,820,480 |

Median: 2,352.709 ms and 8,399.68 product rows/s. The paged run is the closer apply-only proxy for the production page limit, while still remaining synthetic.

## Final closure synthetic 19,762-row full path

This final integrated run uses 20 pages of at most 1,000 products. The timed region includes page apply, immutable `remote_price_id` ownership, authoritative product-reference persistence/relink, pending-price replay, full reconciliation, invariant audit, and exactness evaluation. It excludes HTTP, JSON parsing, staging latency, and WPF rendering.

Evidence: `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-closure-19762-paged-full.trx`.

| Iteration | Products | Prices | Pending | Completeness | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|
| 1 | 19,762 | 19,762 | 0 | Verified | 5,436.762 | 3,634.88 | 6,140.625 | 161,939,456 | 15,253,504 |
| 2 | 19,762 | 19,762 | 0 | Verified | 4,747.793 | 4,162.36 | 4,937.500 | 175,218,688 | 15,253,504 |
| 3 | 19,762 | 19,762 | 0 | Verified | 4,704.507 | 4,200.65 | 4,812.500 | 174,350,336 | 15,253,504 |

Median: 4,747.793 ms, 4,162.36 product rows/s, 4,937.500 ms CPU, and 174,350,336 bytes working set. Every iteration ends with exactly 19,762 active products, 19,762 price-history rows, zero pending prices, and completeness `Verified` against the deterministic summary.

Profiling during integration exposed two expression-wrapped product-identity joins that prevented SQLite from using `idx_products_remote_product_id`. Both now compare the already-canonical `remote_product_id` directly; the catalog checker guards the page reference-cleanup path against reintroducing the per-row `TRIM` scan.

## Validation and remaining evidence

Local regression coverage passes for rollback, clean retry, duplicate identity, pending stock across remote barcode changes and later batches, pending remote prices, tombstones, cancellation, cross-page reference relinking, price-ID collision handling, ACK wildcard precedence, and batch skipped-row accounting. The final consolidated count is `152/152`; release/gate provenance is recorded in the closeout report and external post-commit evidence.

The following must not be inferred from the tables above:

- authenticated Admin Console count or category/supplier totals;
- staging SQLite exactness against 19,762;
- end-to-end `hasMore=false` runtime proof;
- absence of UI freeze during a real staging pull.

Those staging items remain `BLOCKED_EXTERNAL` until credentials and the authorized staging fixture are available. The final local apply + reconcile + verify benchmark is complete; it is synthetic evidence and does not replace the blocked staging runtime proof.
