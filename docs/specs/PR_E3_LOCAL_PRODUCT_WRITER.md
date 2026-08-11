# PR-E3 Local product writer and metadata resolver extraction

## Problem

`ProductRepository` still combines the public product API with local product
writes, local price-history writes, and supplier/category metadata resolution.
Those responsibilities are independent from E1 reads, E2 remote price state,
and the remaining remote catalog identity writer.

## Invariants

- `ProductRepository` keeps every public method and parameter/default-value
  contract; existing WPF, CLI, import, and POS call sites remain unchanged.
- `LocalProductWriter` owns local product/meta/history writes only. It retains
  validation of reserved barcodes, soft-delete/reactivation behavior, local
  price-history atomicity, and preservation of stock while local outbox work is
  unresolved.
- `ProductMetaResolver` owns supplier/category lookup and normalization used by
  local product writes.
- The optional `remoteProductId` path in the public upsert façade remains
  behaviorally unchanged for E4; E3 routes only an empty local remote ID to the
  extracted local writer.
- No collaborator opens or commits an independent transaction in an API that
  receives a caller-supplied `SqliteConnection` and `SqliteTransaction`.
- Remote identity canonicalization, catalog tombstones, remote batch context,
  and remote price history remain outside E3.

## Acceptance criteria

1. `ProductRepository` forwards local public mutations to `LocalProductWriter`
   and retains query and remote-price façades.
2. Local create/update, metadata resolution, price history, soft-delete,
   reserved-barcode rejection, and stock-outbox preservation remain equivalent.
3. The static transaction API used by local import continues to use the caller
   connection and transaction without a nested transaction.
4. Direct collaborator tests prove façade parity, rollback behavior, invalid
   input rejection, and no mutation under concurrent local reads where
   applicable.
5. Static gates assert the façade boundary and local-writer invariants without
   weakening the remote catalog checks reserved for E4.

## Files and risks

- Add `LocalProductWriter` and `ProductMetaResolver` under
  `src/Win7POS.Data/Repositories/`.
- Rewire only `ProductRepository` internals in this slice; public call sites
  stay source-compatible.
- Update the product-boundary static checks and targeted data tests.
- The main risks are accidentally routing a remote upsert through local rules,
  breaking shared transaction ownership, or losing the pending-stock guard.

## Deferred to E4

- Remote catalog product identity canonicalization and tombstones.
- Catalog prepared commands/run context and the final ProductRepository
  decomposition gate.
