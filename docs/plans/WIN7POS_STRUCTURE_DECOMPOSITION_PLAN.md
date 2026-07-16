# Win7POS structure decomposition plan

## Constraints shared by every PR

- WPF remains net48/x86 and Windows 7 SP1 compatible.
- Core and Data remain netstandard2.0; no Windows 10-only APIs or new dependency
  is introduced without a separate compatibility review.
- Public façades remain available while internals move.
- No PR may weaken shop transition fencing, sale-safe decisions, immutable outbox
  payload/SHA256, refund/void economics, remote-ID collision checks, or the atomic
  sale+stock+outbox transaction.
- Each PR is independently revertible and must pass canonical gates, full Core/Data
  tests, CLI selftest and WPF net48/x86 build.

Recommended execution order is **PR 5 → PR 1 → PR 2 → PR 3 → PR 4**. Versioned
migration fixtures establish the persistence safety net first; financial repository
decomposition remains last.

## PR 1 — Extract `PosStartupCoordinator` from `MainWindow`

### Files

- `src/Win7POS.Wpf/MainWindow.xaml.cs`
- new `src/Win7POS.Wpf/Infrastructure/PosStartupCoordinator.cs`
- new immutable startup/refresh outcome types
- focused tests in `tests/Win7POS.Core.Tests` for UI-neutral decision code where
  target-framework boundaries permit it

### Dependencies and boundary

Move DB initialization, trusted-session refresh, online refresh ordering and
startup setting decisions behind an injected coordinator. Keep `Dispatcher`,
dialogs, navigation, progress presentation, window close and localization in
`MainWindow`. Preserve the current operation order, timeout and cancellation
semantics. A safe first commit may extract the background online refresh block,
followed by the remaining startup block in the same PR series.

### Tests

- startup with new, legacy and unavailable DB;
- trusted/untrusted/revoked session outcomes;
- offline and timeout outcomes;
- cancellation before and during refresh;
- existing startup/no-eager-DB, login and sale-safe checkers;
- WPF dispatcher smoke on the current Windows builder.

### Rollback

The coordinator is called through one façade method. Revert the call site and new
files; no schema or persisted-key format changes.

### Win7 risk

MEDIUM: UI-thread ordering and net48 cancellation behavior must remain exact. Use
only existing framework primitives.

### Acceptance

- no UX, dialog, navigation, timeout or persisted-setting change;
- `MainWindow` no longer owns online workflow implementation details;
- all startup outcomes are deterministic and unit-testable;
- canonical and WPF x86 gates remain green.

## PR 2 — Move the catalog state machine out of WPF

### Files

- `src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs`
- new state-machine/orchestrator types in Core/Data as layer ownership dictates
- `src/Win7POS.Data/Online/CatalogShopStateRepository.cs`
- `src/Win7POS.Data/Online/PosOnlineCompatibilityValidator.cs`
- transport contracts only if an additive outcome type is required
- catalog exactness, checkpoint, cancellation and transition-race tests

### Dependencies and boundary

Keep WPF as the adapter for dialogs, progress and user cancellation. Extract a
transport/page runner, pure snapshot-chain validator, response mapper and state
writer. Persistence remains in Data; protocol-neutral state transitions belong in
Core. Do not change cursor, epoch, shop binding, full-repair or sale-safe semantics
while moving code.

### Tests

- `Ok=false` response cannot apply or advance cursor;
- caller cancellation remains cancellation, not timeout;
- duplicate/replayed pages and late responses;
- cursor/catalog-version/summary exactness across multiple runs;
- reset/full-repair versus active pull race with epoch fencing;
- offline and legacy-server compatibility fixtures;
- current catalog pull/import/restore gate suite.

### Rollback

Retain `PosCatalogPullService` as façade. Each extracted collaborator can be
replaced by the previous private implementation without a data migration.

### Win7 risk

MEDIUM: preserve `ConfigureAwait(false)`, timer/backoff behavior, C# 8 and
netstandard2.0 APIs; do not introduce modern channels or runtime-only primitives.

### Acceptance

- WPF contains presentation/cancellation adapters only;
- the state machine runs in headless tests;
- checkpoint and sale-safe writes preserve order and fail-closed behavior;
- no protocol or database compatibility change.

## PR 3 — Split `ProductRepository` internally

### Files

- `src/Win7POS.Data/Repositories/ProductRepository.cs`
- new internal `ProductQueryRepository`
- new internal `LocalProductWriter`
- new internal `RemoteCatalogProductWriter`
- new internal `RemotePriceHistoryRepository`
- existing product, remote catalog, price idempotency and performance tests

### Dependencies and boundary

Keep the current public `ProductRepository` as a delegating façade. Move SQL and
helpers one responsibility at a time. Collaborators must accept the caller's
`SqliteConnection` and `SqliteTransaction`; they may not open independent
connections inside an existing catalog transaction. Preserve `CatalogMetaWriteGate`
and all remote ownership/collision evidence.

### Tests

- current CRUD/search/page exactness;
- local and remote write ownership;
- price history idempotency, pending replay and collision quarantine;
- full-refresh batch exactness and tombstones;
- 20k/100k query plans and import/update benchmark;
- legacy DB upgrade fixtures from PR 5.

### Rollback

Move one collaborator per commit while retaining façade signatures. Reverting a
commit restores methods to the façade without schema change.

### Win7 risk

MEDIUM: connection lifetime, command preparation and transaction scope affect
SQLite behavior and x86 working set.

### Acceptance

- no public API break;
- identical SQL result sets and remote-ID conflict outcomes;
- no extra connection or commit inside batch apply;
- measured performance is no worse and all exactness tests remain green.

## PR 4 — Split `SaleRepository` internally

### Files

- `src/Win7POS.Data/Repositories/SaleRepository.cs`
- new internal `SaleTransactionWriter`
- new internal `SaleReadRepository`
- new internal `SalesSyncOutboxRepository`
- new internal `SaleReversalWriter`
- new internal `SaleStockMovementWriter`
- sales, reversal, lease, outbox binding and sale-safety tests

### Dependencies and boundary

The façade retains public methods. The write path passes one connection and one
transaction to every collaborator. Sale header/lines, local stock movements and
immutable outbox payload/SHA256 remain one commit. Refund/void economic ordering
and reversal idempotency do not change.

### Tests

- sale+stock+outbox all-or-nothing failure injection;
- refund, partial refund, full void, over-refund and repeated reversal;
- outbox immutable payload/hash and shop binding;
- lease acquire/takeover, late ACK, retry and attempt-count CAS;
- concurrent preflight-block versus acquired lease;
- reporting totals and daily net revenue.

### Rollback

One collaborator per commit behind the façade. No schema or payload version change;
revert delegation and restore the previous private methods.

### Win7 risk

MEDIUM/HIGH because financial and inventory atomicity is involved. Avoid new
concurrency primitives or async patterns unsupported by net48.

### Acceptance

- sale+stock+outbox remains one SQLite transaction;
- economic totals and receipts are byte/field equivalent where specified;
- late workers cannot overwrite another attempt;
- no destructive outbox operation and no payload/hash change.

## PR 5 — Introduce `schema_migrations` and upgrade fixtures

### Files

- `src/Win7POS.Data/DbInitializer.cs`
- new migration registry/runner in `src/Win7POS.Data`
- new `schema_migrations` table
- versioned legacy DB fixtures and migration tests
- backup/restore validation tests where migration and recovery interact

### Dependencies and boundary

Record an ordered, immutable migration identifier and checksum or code version.
Bootstrap existing installations by detecting their actual schema, then apply each
migration once in a transaction. Use table rebuilds where SQLite cannot add required
NOT NULL, UNIQUE or FK constraints safely. Do not enable WAL in this PR unless the
separate backup/restore/fallback matrix has already passed.

### Tests

- empty DB and every supported legacy fixture;
- two and concurrent `EnsureCreated` calls;
- injected failure rolls back schema/data while preserving the prior version;
- constraints, indexes, `integrity_check` and `foreign_key_check` after reopen;
- interrupted/partial historical states;
- backup before migration and restore/reopen after migration;
- physical or VM Win7 startup on copied fixtures before release.

### Rollback

Every migration documents whether code rollback is compatible with the upgraded
schema. Prefer additive/backward-compatible steps. For table rebuilds, require a
pre-migration backup and a tested restore path; never attempt an automatic
destructive downgrade.

### Win7 risk

MEDIUM/HIGH: SQLite provider/native DLL and filesystem replacement behavior must be
validated on Win7/x86, including power-loss simulations on a disposable fixture.

### Acceptance

- deterministic version history for new and legacy DBs;
- repeated startup is idempotent;
- no partially applied migration after injected failure;
- strict constraints are equivalent for fresh and upgraded databases;
- documented rollback/restore evidence accompanies every migration.
