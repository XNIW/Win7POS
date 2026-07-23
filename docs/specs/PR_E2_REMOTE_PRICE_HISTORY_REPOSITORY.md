# PR-E2 Remote price-history repository extraction

## Problem

ProductRepository still owns remote price idempotency, deferred-price replay,
ownership evidence, and authoritative-repair quarantine alongside unrelated
product identity and local-write work. This ARCH-003 slice isolates that remote
price state machine while preserving ProductRepository as the public façade.

## Invariants

- ProductRepository keeps its public remote-price methods and their result
  types; existing callers remain source-compatible.
- RemotePriceHistoryRepository owns the remote price idempotency,
  pending queue/replay, ownership, and repair SQL only.
- Transaction-taking operations accept the caller-provided SqliteConnection
  and SqliteTransaction and never open or commit an independent transaction.
- A remote price id remains immutably owned by one remote product. Conflicting
  delta/retry input fails closed before history or pending writes.
- Pending prices retain their exact tuple and replay against the canonical
  active product using the existing 2,000-row bounded loop.
- Only an authoritative full refresh may repair legacy evidence: quarantine
  first, retain history with an unbound id, then establish canonical ownership.
- Local/manual price history, product metadata, remote product identity, and
  tombstones remain outside E2.

## Acceptance criteria

1. ProductRepository delegates all public remote-price apply/queue/replay
   methods to RemotePriceHistoryRepository.
2. Remote catalog batch and catalog-import outbox paths call the extracted
   transaction helpers with their existing connection and transaction.
3. Existing idempotency, collision, queue/replay, authoritative repair, and
   rollback cases exercise the extracted collaborator without changing their
   behavioral assertions.
4. Direct collaborator tests plus an explicit façade-parity case prove
   conflicting-owner rejection,
   pending replay, and rollback preservation where applicable.
5. Canonical gates, Release build, Core/Data tests, x86 WPF build, paging
   smoke, and CLI self-test pass on the exact PR head.

## Files and risks

- Add RemotePriceHistoryRepository under src/Win7POS.Data/Repositories/.
- Keep the public RemotePriceHistoryApplyResult compatibility type in
  ProductRepository.
- Rewire RemoteCatalogBatchRepository and CatalogImportOutboxRepository only
  at their static transaction helper call sites.
- Update catalog-pull static checks so ownership assertions inspect the new
  collaborator and still require façade delegation.
- The principal risk is splitting a transaction boundary or weakening
  ownership evidence; the implementation moves SQL and helper bodies intact.

## Deferred slices

- E3: local product/catalog metadata writes and reference resolution.
- E4: remote catalog product writer, identity/tombstones, and final
  decomposition gate.
