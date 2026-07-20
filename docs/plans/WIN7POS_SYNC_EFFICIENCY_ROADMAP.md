# Win7POS sync efficiency and release-hardening roadmap

## Scope and compatibility rule

This is the sync/performance/release follow-up to PR-B. The PR-B migration delta
does not change `CatalogSyncPolicy`, `CatalogSyncCoordinator`, `MainWindow`
scheduling, sales/catalog-import services, repository paging, HTTP contracts,
payload/hash, idempotency or reversal economics.

Every future contract field is optional. If the current Admin server omits it,
Win7POS must preserve the current 24–36 second catalog cadence, five-second
partial-resume behavior, trusted-session validation, incremental-first policy,
and fail-closed exactness semantics.

## Current-main findings

| ID | Priority | Finding | Evidence / impact |
| --- | --- | --- | --- |
| P1-SYNC-01 | P1 | Auth denial clears trust, but the outer sequential run can continue with the previously captured session. | `PosSalesSyncService.cs:279-290`; `MainWindow.xaml.cs:829-949` |
| P1-SYNC-02 | P1 | Heartbeat, sales, catalog import and catalog are serialized under catalog-oriented scheduling; a sales backlog over 25 rows can wait a full idle interval. | `MainWindow.xaml.cs:829-921`; `CatalogSyncSchedulerPolicy.cs:50-76` |
| P1-PERF-01 | P1 | Per-lane clients and fully buffered/copy/re-encode JSON amplify allocations on x86. | `PosAdminWebClient.cs:109-160,268-330` |
| P1-PERF-02 | P1 | Each catalog page reloads broad local ID maps and full refresh retains/duplicates authoritative CLR sets, approaching pages × catalog work. | `RemoteCatalogBatchRepository.cs:106-154`; `ProductRepository.cs:2003-2069`; `PosCatalogPullService.cs:810-828,978-1004` |
| P1-REL-01 | P1 | Dependency/actions remain incompletely locked; SBOM, signing/timestamp, attestation, reproducibility comparison and automated vulnerability/license gates remain open. | `.github/workflows/*.yml`; repository package inventory |

P0 is `0`. These five open P1 items (four sync/performance plus one composite
supply-chain item) are explicitly deferred to follow-up PRs; none requires
widening PR-B. PR #7 closed former `P1-REL-02`: an explicitly requested local
installer build now fails closed when the compiler or exact output is missing.

## Delivery update — 2026-07-19

- PR #6 (migration foundation) merged normally as `ea85d91`.
- SYNC-1 merged through PR #8 as `6d9a9e0`, closing the pagination,
  revision-compatibility and typed-drain prerequisites described in sections A
  and B.
- `P1-SYNC-01` and `P1-SYNC-02` are `DONE_MERGED` through PR #9 at normal merge
  commit `1be70172cab56895f66389a99b4a6fa92352c7e2`; exact-head and post-merge CI
  and Release Pack were green.
- `P1-PERF-01` and `P1-PERF-02` are implemented on the separate PERF-1 branch:
  endpoint/config-scoped transport, direct bounded streaming, one catalog apply
  context per run, prepared-command/map reuse, page-scoped identity/pending-stock
  queries and a real net48/x86 benchmark. They become `DONE_MERGED` only after
  the independent PR's exact-head checks and normal merge.
- Composite supply-chain item `P1-REL-01` remains an independent repository-wide
  release-hardening follow-up and is not silently reclassified by sync delivery.

## A — Additive Admin heartbeat contract

Add optional fields to `PosHeartbeatResponse`:

| Field | Type | Validation | Current-server fallback |
| --- | --- | --- | --- |
| `catalogRevision` | nullable string | Opaque, trimmed, bounded token; never parse numerically. | Missing means no revision hint and current pull policy remains. |
| `catalogChangesAvailable` | nullable bool | A hint only; it cannot mark sale-safe or advance a cursor. | Missing means current catalog eligibility remains. |
| `nextPollAfterSeconds` | nullable int | Clamp to 5–300, apply jitter; invalid/overflow falls back. | Missing means current 24–36 second policy remains. |

Persist server-observed revision separately from the revision committed by a
successful catalog transaction. A delta can be skipped only when all are true:

- no full repair/manual refresh is pending;
- no partial cursor/checkpoint exists;
- no catalog-import ACK needs reconciliation;
- `catalogChangesAvailable == false`;
- observed and committed revisions match.

`true`, a mismatch, contradictory hints, malformed values or uncertainty must
pull safely. Heartbeat never marks catalog sale-safe and never delays required
authorization renewal.

Required compatibility tests:

- deserialize current-server JSON with all fields absent;
- valid optional fields plus unknown-field tolerance;
- negative, overflow, excessive and malformed poll values;
- revision mismatch overrides `false`;
- partial cursor, repair, manual refresh and import ACK override hints;
- restart retains separate observed/committed values;
- a failed page never advances committed revision.

## B — Typed outbox drain result

Use one immutable result model for sales and catalog-import drains:

| Field | Meaning |
| --- | --- |
| `attempted` | Rows whose guarded pending/retry/stale-in-progress → in-progress lease transition succeeded. |
| `acked` | Guarded ACK transitions committed by this invocation. |
| `retried` | Guarded retry transitions committed by this invocation. |
| `blocked` | Guarded fail-closed transitions committed by this invocation. |
| `remainingDue` | Post-run rows due now, excluding blocked and future retries. |
| `nextRetryAt` | Earliest future retry or lease-expiry Unix UTC milliseconds, nullable. |
| `hasImmediateMore` | Exactly `remainingDue > 0`, computed from a post-run aggregate. |

Invariant: `acked + retried + blocked <= attempted`; CAS losers can make the
inequality strict. Add an outcome wrapper with typed `FailureKind` and redacted
code so shared auth-stop never depends on parsing logs.

Tests cover empty/under/exact/over-limit queues, future retries, blocked-only,
stale leases, CAS loser, mixed outcomes, fairness-capped immediate drains, and
repository agreement for due/retry values.

## C — Four independent lanes with shared trust stop

Introduce a session-scoped supervisor owning an immutable trusted-session
generation/fingerprint, one auth-stop cancellation source, a reused transport,
per-lane due/backoff/single-flight state, and a small network concurrency cap.

| Lane | Eligibility and scheduling | Failure behavior | Current-server fallback |
| --- | --- | --- | --- |
| Heartbeat | Authorization cadence and server-time/lease renewal; priority over long catalog work. | Auth denial clears trust and cancels all lanes once; transient errors back off heartbeat only within lease safety. | Existing heartbeat cadence and response fields. |
| Sales | Immediate fairness-capped repeats while `hasImmediateMore`; otherwise due/retry time. | Transient failure backs off sales only; auth stop is global. | Existing batch 25 and current retry classification. |
| Catalog import | Same typed due logic; an ACK makes catalog-delta eligible. | Transient failure isolates this lane; auth stop is global. | Existing import batch/retry behavior. |
| Catalog delta | Optional revision/change/poll hints; partial checkpoint keeps five-second resume. | Exactness/cursor failures retain current repair/full policy. | Missing hints reproduce current 24–36 second scheduler behavior exactly. |

Relinking creates a new generation. Results from an old generation cannot write
trust, cursor, outbox or scheduler state. Start-of-day consumes the same
supervisor rather than maintaining a second sequential state machine.

Concurrency tests:

- denial during every lane prevents all later network calls;
- relink during in-flight calls rejects stale completion;
- no same-lane overlap and clean shutdown;
- one lane's transient failure does not delay unrelated lanes;
- heartbeat priority during a long full refresh;
- fairness under permanent sales backlog;
- import ACK immediately enables catalog eligibility.

## D — Performance roadmap

### Reused HTTP transport and direct bounded streaming

- Reuse one endpoint/config-scoped `HttpClient`; replace only when endpoint
  options change.
- Use `HttpCompletionOption.ResponseHeadersRead`.
- Reject excessive declared `Content-Length`, then enforce the existing 8 MiB
  cap with a counting stream for chunked/no-length responses.
- Deserialize `DataContractJsonSerializer` directly from the bounded stream.
- Deserialize bounded error DTOs without logging response bodies.
- Preserve cancellation, TLS 1.2, request IDs and current-server wire shape.

Tests: declared/chunked oversize, cap crossing, empty/malformed JSON,
cancellation, early disposal, connection reuse, server error DTO and secret-body
logging canaries.

### Context reuse per catalog run

- Build one apply context per pull/run with reusable commands.
- Keep small category/supplier maps for the run and update as reference pages
  arrive.
- Collect only current-page product IDs, barcodes, tombstones and references;
  query in bounded chunks or through a page staging table.
- Query pending stock only for current-page products.
- Preserve transaction-per-page, cursor CAS, identity conflict, tombstone,
  exactness, no-hard-delete and immutable ownership behavior.
- For full exactness, stream authoritative IDs into a generation-keyed durable
  SQLite staging table introduced by a later append-only migration after 0007;
  never overload or mutate 0007. Do not use a connection-local temp table across
  page connections.

### Keyset product pagination

Use a filter-bound `{barcode,id}` cursor:

```sql
WHERE barcode > @barcode
   OR (barcode = @barcode AND id > @id)
ORDER BY barcode, id
LIMIT @take
```

Cache anchors for backward navigation and reset on filter/catalog revision
changes. Keep a deliberate indexed-anchor/offset fallback where arbitrary page
jumps cannot resolve a cursor. Tests cover inserts/deletes between pages,
duplicates/collation, no skip/duplicate, filter fingerprint mismatch, exact
barcode precedence, index use and stable 100k last-page p95.

### Bounded asynchronous logging

- One bounded process queue and background writer.
- Batch append/rotation on the writer only.
- Never block UI or sale paths; coalesce/drop INFO first, reserve warning/error
  capacity, expose drop/high-water metrics.
- Short bounded shutdown flush and critical-only synchronous fallback.
- Redact before persistence and never enqueue mutable secret-bearing payloads.

Profile before implementation. Test saturation, ordering, slow-disk caller
latency, redaction, rotation, shutdown timeout, writer failure and bounded x86
memory.

## Benchmark and regression contract

- Synthetic rows: 2,000, 19,763 and 100,000.
- Page sizes: 100 and 1,000; full and incremental paths.
- Assert lookup commands/rows scale with page references, not pages × catalog.
- Add a real `net48/x86` harness sampling peak private bytes, peak working set,
  GC counts, elapsed and CPU throughout the run.
- Establish a same-host baseline and block >10–15% elapsed or peak-memory
  regression unless reviewed.
- Retain rollback, pending-stock, tombstone, late-reference, ownership,
  identity-conflict and concurrency-fence tests.

PERF-1 now includes net10/x64 and actual net48/x86 process sampling throughout
the run. The 19,763-row full path is `Verified` in 3/3 iterations on each runtime;
the x86 median is 6,427.448 ms, peak working set 78,008,320 bytes, peak private
bytes 62,595,072 and maximum dispatcher delay 24.528 ms. This is a Windows-host
x86 harness, not physical Windows 7 certification.

## Release and supply-chain follow-up

- PR #7 already closed fail-closed installer generation, exact clean-tree
  provenance/manifests, privacy/secret rejection and Win7 runtime inventory.
- Add least-privilege permissions to the remaining CI workflow and set
  `persist-credentials: false` on every checkout.
- Pin Actions to full commit SHAs with controlled dependency updates.
- Generate NuGet lock files and restore in locked mode.
- Pin the Inno Setup version and verify the compiler/package hash.
- Derive one protected-tag semantic version across assembly, `VERSION`,
  installer metadata and filenames.
- SHA-256 Authenticode-sign binary/installer with RFC3161 timestamp compatible
  with Win7 and verify after signing.
- Produce SPDX/CycloneDX SBOM, vulnerability/deprecated-package/license checks,
  secret/history scanning and CodeQL-equivalent analysis.
- Attest provenance/checksums; keep signing secrets restricted to protected tag
  environments. PR builds stay unsigned but reproducible.
- Two clean builds at one SHA compare normalized manifests and payload hashes.

`windows-latest` validates tooling, not Windows 7 runtime compatibility.
Physical Win7 SP1 install/startup/uninstall remains an external certification
item.

## Recommended PR sequence

1. Admin backend `catalog-v2`: stable snapshot/revision/summary and deterministic
   continuation, in its own server repository PR.
2. `SYNC-1`: ambiguous-end guard, optional Admin fields, observed/committed
   revision, typed outbox result and compatibility tests.
3. `SYNC-2`: generation-scoped supervisor, shared auth-stop, independent lanes
   and start-of-day integration.
4. `PERF-1`: reused transport, bounded direct-stream deserialization, per-run
   apply context, page-scoped lookups and real x86 benchmark evidence.
5. `PERF-2`: authoritative-ID staging migration, keyset paging and bounded
   logging after profiling.
6. `RELEASE-1`: locked dependency/actions/toolchain, unified version, SBOM,
   signing/timestamp, attestation and reproducibility comparison.

Every step retains a tested current-server fallback.
