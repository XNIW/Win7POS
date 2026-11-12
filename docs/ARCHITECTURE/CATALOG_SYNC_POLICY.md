# Catalog synchronization policy

## Purpose

Win7POS treats incremental synchronization as the normal catalog path. A full
catalog is an evidence-driven repair operation, never a periodic refresh strategy.
The policy is pure Core code; persistence, network transport and WPF presentation
consume its decision without redefining it.

The implementation keeps the existing compatibility boundaries: Core and Data
remain `netstandard2.0`, WPF remains `net48`, `x86`, `Prefer32Bit`, and no
Windows 10-only UI or runtime dependency is used.

## Decision model

`CatalogSyncPolicy` returns one of three modes:

- `Incremental`: continue from the committed shop cursor;
- `ResumeIncremental`: continue a validated partial delta checkpoint;
- `Full`: establish a new authoritative generation for a specific repair reason.

Normal triggers resolve only to incremental or resume:

- start of day;
- periodic scheduler tick;
- application foreground;
- network recovery;
- manual **Synchronize now**;
- catalog-import acknowledgement;
- partial-resume catch-up.

The full path is allowed only when one of these reasons is present and the caller
has the corresponding evidence/authority:

- `FirstBootstrap`;
- `MissingShopBinding` or `MissingLegacyCursor` when legacy state cannot be
  recovered safely;
- `CursorRejectedOrExpired` or `ServerRequestedReset`;
- `ShopChanged`;
- `RestoreRecovery`;
- `ExactnessRepair` after mismatch or `repair_required` evidence;
- `AdministratorRepair` with `DbMaintenance` permission and confirmation;
- `MigrationInvalidatedCursor` when a migration explicitly declares the old
  cursor incompatible.

The following conditions never select full by themselves:

- timeout, offline state or transient network failure;
- authentication denial;
- stale age or an ordinary daily/periodic trigger;
- manual synchronization;
- retry/backoff exhaustion;
- catalog-import ACK;
- foreground or network recovery;
- partial page budget exhaustion.

Failures retain their typed outcome. They do not silently fall through to full.

## Page and checkpoint ordering

For each delta page the order is:

1. validate the captured trusted session, shop binding, transition epoch, mode,
   previous cursor and pinned snapshot evidence;
2. call the Admin Web transport with the committed cursor;
3. require transport success, a response value and `Ok=true`;
4. validate compatibility, shop identity, catalog version, summary and cursor
   progress;
5. apply the page in one SQLite transaction;
6. commit the page;
7. persist the new cursor/checkpoint using compare-and-swap evidence.

Cursor progress is forbidden before page commit. Critical skipped rows fail closed
and request repair. Replayed pages remain idempotent. When `hasMore=true` exceeds
the current run budget, the validated delta checkpoint is retained and the result
is partial; the coordinator schedules resume instead of full.

## Transition and late-response fencing

`CatalogShopTransitionBarrier` serializes catalog transitions. Persistent catalog
state carries a monotonically changing epoch. Page apply, cursor storage,
exactness completion and repair completion verify:

- expected shop ID and shop code;
- expected epoch;
- expected previous mode;
- expected previous cursor.

A restore reset or another authoritative transition changes this evidence. A
response captured before that transition therefore cannot commit afterwards.
When the server selects `full_refresh`, the client first establishes a new fenced
generation and repeats the first request; rows captured under the old epoch are
not applied to the new generation.

Caller-requested cancellation is rethrown. An internal timeout is represented as
`timeout` and its diagnostic write is generation-fenced, so a late timeout cannot
overwrite a newer successful generation.

Sales synchronization uses the same stale-worker principle. Preflight reads the
outbox ID, sale ID, status, attempt count and lease evidence, then blocks only via
compare-and-swap. A worker cannot overwrite a lease acquired by another attempt.
ACK handling also requires transport success, a response value and `Ok=true`.

## Coordinator and schedule

The ordinary shell owns one `PosOnlineSyncSupervisorHost` and one
generation-scoped `OnlineSyncSupervisor`. Heartbeat, sales, catalog imports,
catalog pull, article mutations, images, orders and historical payments use independent single-flight
lanes. Concurrent triggers coalesce per lane; there is no serial dependency on a
successful catalog request before a sale can be sent. `CatalogSyncCoordinator`
remains a compatibility adapter, not the global scheduler of the ordinary shell.

The adaptive schedule uses:

- immediate resume catch-up after 5 seconds;
- idle success polling with randomized 24-36 second spacing;
- retry delays of 5, 15, 30, 60, 120 and 300 seconds with ±20% jitter;
- endpoint-offline failures retain quiet bounded retry polling even while the Windows NIC remains up;
- reset of the failure backoff after success;
- polling stop after authentication denial.

The shared request gate admits at most two requests: heartbeat has priority 3,
sales/orders 2, catalog import/pull 1, articles/images 0. A lane releases its
request slot between requests. Durable per-entity dependencies and blocked rows
do not become a global queue lock. A healthy periodic trigger does not schedule
full; independently persisted repair evidence can still require full.
The history lane also uses priority 0, bounded two-operation delivery and
eight-page receive turns. It polls at 25-35 seconds when idle and resumes
immediate work at 1-5 seconds. Historical discovery is signalled without
extending the commercial start-of-day/foreground result. A history permission
denial remains domain-scoped; a genuine shared authorization stop still stops
all credentialed requests. Readable replica publication notifies open views
after the short report boundary, without a screen-owned network timer.
After normal POS opening the scheduler remains active even when start-of-day has
already completed. Ordinary pending, retry and in-progress sales never block the
register; the 25-row sales batches continue on scheduled runs until the measured
outbox counters reach zero. `failed_blocked` remains an operator-action condition.

The ASUS019 historical extension uses this same supervisor, a separate domain
cursor/outbox and bounded turns. Its implementation/qualification status is in
`docs/plans/ASUS-W7POS-019_GETNET_HISTORY/CLOUD-CONTINUATION.md`; the presence of
this policy is not evidence that the extension is delivered. UI input latency
(250 ms) and end-to-end convergence latency are measured separately.

## Diagnostics and UI contract

Safe coordinator diagnostics use the `pos.catalog.sync.` prefix and include the
last mode, trigger, full reason, pages, rows, duration, success timestamps, partial
resume count, incremental/full totals and full ratio. Cursor values are exposed to
the UI only as a redacted fingerprint.

The Sync Center shows status, trigger, mode, progress, last success/error and
partial-resume state. **Synchronize now** and retry always request incremental
policy evaluation. **Full repair** is a separate permission-gated action with an
explicit confirmation and visible reason. A running full repair cannot be closed
mid-generation; incremental work remains cancellable.

All new UI copy is present in Italian, English, Spanish and Chinese resources.

## Verification contract

The automated contract is covered by policy, resume, concurrency, restore race,
sales preflight CAS, scheduler and WPF static-gate tests. The final 2026-07-17 run
recorded:

- 34/34 policy cases;
- 100/100 normal trigger cycles in incremental/resume mode;
- 30/30 canonical gates;
- 248/248 Core/Data tests with zero skipped;
- WPF `net48/x86` build with zero warnings and zero errors.

Authenticated staging, a physical Windows 7 SP1 run and attached POS hardware are
separate runtime evidence and must not be inferred from these automated results.

### Operator history requests and the shared gate — ASUS019

History preparation, authority and freeze acknowledgement use the active host's
existing credentialed request executor. The expected generation is checked before
admission, live credentials are read after acquiring the same two-slot priority
gate, and a response cannot cross a generation change. Caller cancellation owns
only that foreground request; shutdown/relink cancel and drain it with lane work.
No commercial maintenance or gate spans HTTP. This bridge adds no scheduler or
durable queue. History-only403 remains scoped; genuine auth_denied uses the shared
credential-stamp revocation guard. Incremental v2 admission/ingestion remains WIP.
