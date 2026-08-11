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

| ID | Priority | Original finding | Evidence / impact | Current status |
| --- | --- | --- | --- | --- |
| P1-SYNC-01 | P1 | Auth denial could clear trust while the outer run continued with a captured session. | Generation/trust fences and denial tests delivered in PRs #8, #9 and #11. | `DONE_MERGED` |
| P1-SYNC-02 | P1 | Heartbeat, sales, catalog import and catalog were serialized under catalog-oriented scheduling. | Four generation-scoped lanes and shared auth stop delivered in PR #9. | `DONE_MERGED` |
| P1-PERF-01 | P1 | Per-lane clients and fully buffered/copy/re-encode JSON amplified x86 allocations. | Reused transport and bounded direct streaming delivered in PR #10. | `DONE_MERGED` |
| P1-PERF-02 | P1 | Broad page lookups and retained full-refresh authoritative CLR sets approached pages × catalog work. | PR #10 scoped page work and PR #21 delivered durable ID staging; PERF2-B/C separately closed the planned paging/logging follow-ups. | `DONE_MERGED` |
| P1-REL-01 | P1 | Release inputs, supply-chain evidence, reproducibility and signing were incomplete. | PRs #13 and #20 close the repository-local chain; real production signing/timestamp remains external. | `PARTIAL_EXTERNAL_SIGNING` |

P0 is `0`. `P1-SYNC-01`, `P1-SYNC-02`, `P1-PERF-01` and `P1-PERF-02` are
`DONE_MERGED`. Composite `P1-REL-01` is `PARTIAL_EXTERNAL_SIGNING`: every
repository-local acceptance criterion is merged and green, while a real
protected-tag certificate and RFC3161 timestamp have not been exercised.

## Delivery update — 2026-07-21

- PR #6 (migration foundation) merged normally as `ea85d91`.
- SYNC-1 merged through PR #8 as `6d9a9e0`, closing the pagination,
  revision-compatibility and typed-drain prerequisites described in sections A
  and B.
- `P1-SYNC-01` and `P1-SYNC-02` are `DONE_MERGED` through PR #9 at normal merge
  commit `1be70172cab56895f66389a99b4a6fa92352c7e2`; exact-head and post-merge CI
  and Release Pack were green.
- PERF-1 is `DONE_MERGED` through PR #10 at normal merge commit
  `0ad16bd2c40454c07d0ffb6e4908e23b89b4a5a1`, without force push. Exact-head CI
  run `29874926694` and Catalog Performance run `29874958148` passed on
  `92e1c323d55ea7b00f3f8bfa35f32fe2fb28e391`; post-merge CI run `29875846523`
  and Release Pack run `29875846496` passed on the final main SHA.
- Composite supply-chain item `P1-REL-01` remains an independent repository-wide
  release-hardening follow-up and is not silently reclassified by sync delivery.
- CLOSEOUT-DOCS PR #12 merged normally as `939ca843`; RELEASE1-A PR #13 merged
  normally as `1832dcca`; RELEASE1-B PR #20 merged normally as `5313ff36`.
  The release chain now has full-SHA Actions, disabled checkout credential
  persistence, SDK `10.0.301`, seven NuGet lock files, one semantic version,
  verified Inno Setup `6.7.3`, CycloneDX, security gates, reproducibility,
  checksums, provenance and unsigned attestations.
- PERF2-A PR #21 merged normally as `81acd479` with immutable migration
  `0009-catalog-authoritative-id-stage`; PERF2-B PR #22 merged normally as
  `63152222`; PERF2-C PR #23 merged normally as `0c5052f3`.
- Final PERF-2 post-merge runs on `0c5052f3` are CI `29908121321`, Security
  Supply Chain `29908121286`, Release Pack `29908121289` and Catalog Performance
  `29908134313`, all `completed/success`.

## Delivery update — 2026-07-23

- SQLite durability is `DONE_MERGED` through PR #25 (`3887262`); the policy
  explicitly exercises DELETE journal mode, FULL synchronous mode, foreign keys,
  busy handling and bounded temporary/cache behavior without introducing a WAL
  requirement.
- ARCH-005 is `DONE_MERGED` through PR #26 (`56b3803`), PR-C through PR #27
  (`d6751576`), and ProductRepository/ARCH-003 through façade-preserving PRs
  #28-#31 (`6556a85`, `f2175ab`, `bc52904`, `38cf284`).
- SaleRepository/PR-F is `DONE_MERGED` through façade-preserving PRs #32-#38,
  ending at normal merge `cc9f02c`. The temporary concurrent monotonic-settings
  reservation fix in PR #34 is included in that validated sequence.
- PERF-05 is `DONE_MERGED` through PR #39 at normal merge
  `93a5e4afa819f0de14513ddb7603091433d917ba`. It adds observational,
  page-local remote-price SQL diagnostics published only after commit. The
  controlled fresh-price fixture for `N=3`, `P=2` proves 19 commands and 22 SQL
  statements; it is neither a set-based rewrite nor a performance threshold.
- PR #39 exact-head CI/Security/Release Pack runs `30005059126`, `30005059146`
  and `30005117027`, and post-merge runs `30005966795`, `30005966771` and
  `30005966770`, all completed successfully on their exact SHAs.
- `P1-REL-01` remains `PARTIAL_EXTERNAL_SIGNING`; physical Windows 7 remains
  `DEFERRED_BY_USER`. Neither status is promoted by repository-local evidence.

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
  SQLite staging table introduced by the next append-only migration after
  `0008-online-sync-generation`, expected
  `0009-catalog-authoritative-id-stage`. Never overload or mutate `0007` or
  `0008`. Do not use a connection-local temp table across page connections.

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

PERF-1 is `DONE_MERGED` and includes net10/x64 and actual net48/x86 process
sampling throughout the run. The 19,763-row full path is `Verified` in 3/3
iterations on each runtime; the x86 median is 6,427.448 ms, peak working set
78,008,320 bytes, peak private bytes 62,595,072 and maximum dispatcher delay
24.528 ms. This is a Windows-host x86 harness, not physical Windows 7
certification.

PERF-2 is also `DONE_MERGED`. PR #21 replaced cross-page CLR authority with a
generation/full-run/shop-keyed durable SQLite stage and bounded cleanup. PR #22
uses filter/revision-bound `{barcode,id}` cursors for ordinary next/previous
navigation and keeps an explicit OFFSET fallback only for unanchored arbitrary
jumps. Its 100,000-row repository measurement recorded p95 `40.587 ms` for
keyset versus `86.186 ms` for OFFSET (`2.12x`), while the raw SQL measurement
was `0.302 ms` versus `17.638 ms` (`58.42x`). PR #23 uses one bounded process
queue/background writer; its x86 saturation smoke observed producer p95
`708.60 us`, maximum call `6.243 ms`, queue high-water `256/256`, `102,817`
deliberately dropped INFO records, private-byte high-water delta `6,287,360`,
peak private bytes `28,389,376` and peak working set `37,584,896`. WARN/ERROR
reserved-capacity, redaction, rotation, writer failure and bounded shutdown
tests passed. These are controlled Windows x86 measurements, not physical
Windows 7 certification.

PERF-05 is `DONE_MERGED` as a bounded observability slice. It records only
remote-price apply command/statement evidence in a fresh page accumulator and
merges it into run diagnostics only after `tx.Commit()`. The exact 3-price/
2-page baseline is 19 commands / 22 statements; rollback tests prove that a
failed page publishes no counters. It does not impose a timing/allocation
threshold and does not claim a set-based price rewrite.

## Release and supply-chain follow-up

- PR #7 already closed fail-closed installer generation, exact clean-tree
  provenance/manifests, privacy/secret rejection and Win7 runtime inventory.
- RELEASE1-A PR #13 delivered explicit least-privilege permissions across its
  four workflows, 12/12 full-SHA Action pins, `persist-credentials: false` on
  all three checkout uses, SDK `10.0.301`, seven lock files with locked restore,
  semantic version `1.0.0`, and official Inno Setup `6.7.3` installer SHA-256
  `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`.
- RELEASE1-B PR #20 extended that invariant to the final six workflows, 32/32
  full-SHA Action pins and 8/8 credential-safe checkouts. It also delivered
  CycloneDX 1.6 (99 components), zero-known-vulnerable/deprecated package gates,
  an approved 99-package license inventory, Gitleaks worktree/history scans,
  CodeQL, two-clean-build payload comparison, SHA-256 manifests, exact-commit
  provenance and unsigned attestations. PR/branch builds remain unsigned and
  require no signing secret.
- The protected-tag workflow and self-signed fixture prove fail-closed wiring,
  but they do not prove production identity. Real Authenticode certificate use,
  RFC3161 timestamping and verification on a protected `vMAJOR.MINOR.PATCH` tag
  remain `BLOCKED_EXTERNAL`; `P1-REL-01` therefore remains
  `PARTIAL_EXTERNAL_SIGNING`.

`windows-latest` validates tooling, not Windows 7 runtime compatibility.
Physical Win7 SP1 install/startup/uninstall remains an external certification
item.

## Closure state and next work

`SYNC-1`, `SYNC-2`, `PERF-1`, RELEASE1-A, the repository-local RELEASE1-B work,
PERF2-A/B/C, SQLite durability, ARCH-005, PR-C, PR-E/ARCH-003, PR-F and PERF-05
are `DONE_MERGED` through independent normal merges. No further repository-local
structural or historical-P2 slice remains open.

The next evidence-bearing action is not deployment: first provide the
authoritative source-provenance/recovery reconciliation manifest and verified
backup for the eight remote-only Supabase staging migrations. Until that is
available, no migration may be repaired, rebased, baselined, reverted or deployed
and no staging E2E result can be claimed. After authorized reconciliation, an
isolated staging deployment/migration and full, incremental and recovery E2E
runs may be planned. Production signing and physical Windows 7/hardware remain
separate external gates.

## Historical P2 reassessment

The nine P2 findings from the 2026-07-16 optimization audit were completed by
independent normal merges with their own validation. The final 2026-07-23
PERF-05 post-merge workflows provide cumulative integration evidence.

| ID | Status | Current evidence / action |
| --- | --- | --- |
| ARCH-003 | `DONE_MERGED` | PRs #28-#31 extracted product query, remote price history, local write and remote catalog product write responsibilities behind preserved façades. |
| ARCH-005 | `DONE_MERGED` | PR #26 extracted the immutable catalog sale-safety policy from state persistence. |
| ARCH-006 | `DONE_MERGED` | Commit `1d53bea2` makes the canonical architecture gate compare all classified projects with `Win7POS.slnx` and fail unknown/missing entries. |
| SQLITE-DURABILITY-001 | `DONE_MERGED` | PR #25 makes DELETE/FULL/foreign-key/busy/temp/cache policy explicit and regression-tested without claiming a WAL migration. |
| SYNC-03 | `DONE_MERGED` | Commit `ecb41239` and cancellation tests distinguish caller cancellation from internal timeout. |
| SYNC-05 | `DONE_MERGED` | Commit `bb4178b5` requires a nonblank canonical catalog version before response/checkpoint persistence and proves invalid versions cannot advance the cursor. |
| PERF-02 | `DONE_MERGED` | PERF2-B normal merge `63152222` uses keyset paging for ordinary navigation with explicit arbitrary-jump fallback and 100,000-row guards. |
| PERF-05 | `DONE_MERGED` | PR #39 adds commit-published command/statement diagnostics with an exact fresh-price 19/22 baseline and rollback non-publication coverage; no set-based rewrite is claimed. |
| E-CI-003 | `DONE_MERGED` | Commit `252fad20` cancels superseded PR CI, while main/protected release runs remain non-cancellable. |

Result: nine `DONE_MERGED`, zero `OPEN`, zero `PARTIAL`, zero `SUPERSEDED`.
No repo-local P2 slice remains unresolved.
