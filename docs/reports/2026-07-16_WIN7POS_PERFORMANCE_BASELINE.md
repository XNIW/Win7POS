# Win7POS performance and SQLite baseline — 2026-07-16

## Environment and rules

- Host: Windows 10.0.26200, x64 host process
- SDK: `C:\Dev\dotnet10\dotnet.exe`, 10.0.301
- Application target retained: WPF net48, x86, Windows 7 SP1 first
- Baseline data root: `C:\POSData\Win7POSOptimizationBaseline-20260716-193139`
- Final data root: `C:\POSData\Win7POSOptimizationFinal-20260716-193139`
- All SQLite probes used unique temporary DBs; no production DB was opened.
- Timings are synthetic host measurements, not physical Win7 or hardware results.

## Baseline software validation

Before audit patches:

- solution restore/build: PASS, 0 warnings/errors;
- canonical gates: PASS;
- Core/Data: 178/178 PASS;
- CLI selftest: PASS;
- WPF net48/x86: PASS.

Baseline Core TRX: `tests/Win7POS.Core.Tests/TestResults/baseline.trx`.

## Catalog apply benchmark

Each mode used three fresh temporary databases. Medians are wall-clock medians;
working set is the process value observed at sample completion, not a sampled peak.

### Baseline before audit patches

| Scenario | Samples (ms) | Median | Max observed working set | Exactness |
| --- | --- | ---: | ---: | --- |
| legacy, 2,000 | 15425.118; 14355.109; 15748.216 | 15425.118 ms | 95,186,944 B | products/prices exact; pending 0 |
| batch, 2,000 | 405.346; 320.589; 362.770 | 362.770 ms | 105,267,200 B | products/prices exact; pending 0 |
| batch-paged-full, 19,762 | 4985.610; 4414.031; 4854.649 | 4854.649 ms | 180,367,360 B | `Verified` in 3/3; pending 0 |

Baseline 2,000-row ratio: **42.52x** batch/legacy.

Artifacts:

- `tests/Win7POS.Core.Tests/TestResults/baseline-catalog-performance.trx`
- `tests/Win7POS.Core.Tests/TestResults/baseline-catalog-performance-19762.trx`

### Final audit branch

| Scenario | Samples (ms) | Median | Max observed working set | Exactness |
| --- | --- | ---: | ---: | --- |
| legacy, 2,000 | 18022.858; 17765.034; 17710.360 | 17765.034 ms | 92,631,040 B | products/prices exact; pending 0 |
| batch, 2,000 | 346.465; 305.831; 311.352 | 311.352 ms | 106,344,448 B | products/prices exact; pending 0 |
| batch-paged-full, 19,762 | 5611.223; 4991.726; 4999.051 | 4999.051 ms | 182,624,256 B | `Verified` in 3/3; pending 0 |

Final 2,000-row ratio: **57.06x** batch/legacy. The automated lane now
requires at least 5x and fails if a requested mode does not return all samples.

Artifacts:

- `tests/Win7POS.Core.Tests/TestResults/catalog-performance.trx`
- `tests/Win7POS.Core.Tests/TestResults/catalog-performance-19762.trx`

The before/final variance is not attributed to a runtime optimization: this branch
did not change catalog apply code or production indexes. It is host-run variance.

## Product query plans: 20k and 100k

The test used the production page-query shape and created the proposed composite
indexes only inside the temporary database after recording the baseline plan.

| Rows | Query | Before plan/median | Candidate-index plan/median | Decision |
| ---: | --- | --- | --- | --- |
| 20,000 | page | products PK index scan; 0.109 ms | unchanged; 0.058 ms | no relevant plan change |
| 20,000 | category | `SCAN m`; 0.911 ms | covering `(category_id, barcode)`; 0.286 ms | synthetic benefit |
| 20,000 | supplier | `SCAN m`; 0.911 ms | covering `(supplier_id, barcode)`; 0.309 ms | synthetic benefit |
| 20,000 | barcode through page search | products PK index scan; 2.378 ms | unchanged; 2.036 ms | candidate indexes do not help |
| 20,000 | name contains | products PK index scan; 2.749 ms | unchanged; 2.635 ms | leading-wildcard scan remains |
| 100,000 | page | products PK index scan; 0.082 ms | unchanged; 0.075 ms | no relevant plan change |
| 100,000 | category | `SCAN m`; 17.074 ms | covering `(category_id, barcode)`; 7.637 ms | synthetic benefit |
| 100,000 | supplier | `SCAN m`; 15.809 ms | covering `(supplier_id, barcode)`; 10.391 ms | synthetic benefit |
| 100,000 | barcode through page search | products PK index scan; 19.881 ms | unchanged; 21.203 ms | no benefit observed |
| 100,000 | name contains | products PK index scan; 21.538 ms | unchanged; 24.188 ms | no benefit observed |

Artifact: `tests/Win7POS.Core.Tests/TestResults/optimization-safeguards.trx`.

Decision: do **not** add the two indexes in this batch. Filter benefit is real in
the synthetic plan, but no import/update regression matrix has yet been run and the
unfiltered, barcode and contains paths do not improve. FTS was not introduced.

## SQLite policy observation

Both a new DB and a legacy probe DB produced:

- `journal_mode=delete`;
- `synchronous=2` (`FULL`);
- `foreign_keys=1` on each connection;
- `busy_timeout=5000` on each connection;
- `integrity_check=ok` after close/reopen;
- idempotent double `EnsureCreated`;
- injected transaction rollback left no row;
- competing writer returned `SQLITE_BUSY` after 5,449 ms, then succeeded after
  owner rollback;
- `wal_checkpoint(FULL)` returned no WAL frames (`-1/-1`) as expected in DELETE
  journal mode.

WAL and fallback were intentionally **not activated or claimed as tested**.
Changing journal mode before validating raw-copy backup, restore replacement and
sidecar behavior would increase recovery risk. The required future matrix includes
new/legacy/replaced DBs, concurrent initialization, WAL checkpoint/reopen,
non-WAL fallback, online backup, restore with pending frames, `foreign_key_check`,
and Windows 7 performance with `synchronous=FULL`.

## Profiled risks not optimized automatically

- `CatalogProductBatchContext.LoadAsync` and reference/product maps are rebuilt per
  page; measure page sizes 100/1000 and statement/allocation counts before changing
  transaction ownership.
- Catalog price apply performs repeated owner/product/history lookups; preserve
  idempotency, evidence quarantine and collision fail-closed behavior.
- Sales sync processes 25-row outbox batches; no evidence justified changing the
  batch or lease policy in this run.
- `PosAdminWebClient` enforces an 8 MiB cap but duplicates large buffers through
  byte/string/byte conversion; measure with an x86 near-limit payload.
- Synchronous file logging was identified for profiling, but no latency evidence
  justified a logging rewrite.
- The 19,762 run observed up to 182,624,256 bytes working set on an x64 test host;
  a true x86/net48 peak/private-set and GC harness remains required.
