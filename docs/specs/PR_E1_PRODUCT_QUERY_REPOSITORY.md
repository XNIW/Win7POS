# PR-E1 Product query repository extraction

## Problem

ProductRepository currently combines read queries, local catalog writes, remote
catalog identity, and price-history replay. This first ARCH-003 slice isolates
only the read side without changing the public facade consumed by WPF, CLI,
imports, or catalog synchronization.

## Invariants

- ProductRepository remains the public sealed facade; existing callers and
  return types remain source-compatible.
- ProductQueryRepository owns only read-only product/detail/paging/statistics
  queries and opens no write transaction.
- Keyset/offset page-plan validation, ordering, filtering, totals, and snapshot
  transaction semantics remain byte-for-byte equivalent in result behavior.
- Local writes, remote product identity/tombstones, remote price-history
  idempotency/replay, and catalog metadata mutation remain outside this slice.
- net48/x86 compatibility and bounded product paging are preserved.

## Acceptance criteria

1. Query, paging, and catalog-statistics methods delegate from
   ProductRepository to ProductQueryRepository.
2. Existing facade callers continue to pass paging, import, POS, CLI, and
   performance tests unchanged.
3. Direct query-repository tests prove facade parity, reject mismatched
   filter/page plans, keep parallel reads stateless, and preserve 900-item
   SQLite batch handling.
4. The canonical gates, Release build, Core/Data suite, x86 WPF build, paging
   smoke, and CLI self-test pass on the exact PR head.

## Files and risks

- Add ProductQueryRepository under src/Win7POS.Data/Repositories/.
- Keep forwarding methods and the compatibility snapshot type in
  ProductRepository.
- Extend paging tests with direct query and facade-parity coverage.
- The principal regression risk is altering keyset cursor ordering or losing
  the count/page snapshot transaction boundary; those paths are moved intact.

## Deferred slices

- E2: remote price-history ownership, replay, idempotency, and quarantine.
- E3: local product/catalog metadata writes and reference resolution.
- E4: remote catalog product writer and final decomposition gate.
