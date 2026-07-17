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

`CatalogSyncCoordinator` provides single-flight execution per trusted shop/session.
Concurrent triggers set a dirty bit and coalesce into at most one follow-up run per
drain. A drain therefore executes at most two runs and cannot create an unbounded
queue.

The adaptive schedule uses:

- immediate resume catch-up after 5 seconds;
- idle success polling with randomized 24-36 minute spacing;
- retry delays of 5, 15, 30, 60, 120 and 300 seconds with ±20% jitter;
- reset of the failure backoff after success;
- polling stop after authentication denial.

Each coordinated run preserves the established operation order: heartbeat, sales
outbox, catalog-import outbox, then catalog pull. No periodic path schedules full.

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
- 29/29 canonical gates;
- 204/204 Core/Data tests with zero skipped;
- WPF `net48/x86` build with zero warnings and zero errors.

Authenticated staging, a physical Windows 7 SP1 run and attached POS hardware are
separate runtime evidence and must not be inferred from these automated results.
