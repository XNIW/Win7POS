# PR-E4 Remote catalog product writer extraction

## Goal

Complete ARCH-003 by reducing `ProductRepository` to its public compatibility
façade and moving the remaining remote catalog product mutation ownership to
`RemoteCatalogProductWriter`.

## Ownership after E4

- `ProductQueryRepository` owns product reads.
- `LocalProductWriter` owns local product, metadata and local price-history
  mutations.
- `RemotePriceHistoryRepository` owns remote price state and replay.
- `RemoteCatalogProductWriter` owns remote product identity, canonicalization,
  duplicate deactivation, tombstones, prepared product commands and the remote
  product batch context.
- `ProductMetaReference` and `ProductIdentityPolicy` are small shared internal
  value/policy types, not `ProductRepository` implementation details.

## Invariants

- The public `ProductRepository` method contracts remain source-compatible.
  Blank or whitespace `remoteProductId` values use the local writer; nonblank
  IDs use the remote writer.
- Batch static cores receive and reuse the caller's `SqliteConnection` and
  `SqliteTransaction`; they do not open, commit, roll back or acquire another
  mutation gate.
- Autonomous remote façade calls acquire the shared `CatalogMutationGate`.
  `RemoteCatalogBatchRepository` and `CatalogFullRefreshReconciler` keep their
  existing ownership of that non-reentrant gate.
- Prepared-command transaction rebinding/reset and page-state publication only
  after commit remain in `RemoteCatalogBatchRepository`.
- Canonical barcode changes preserve unresolved local stock, product metadata
  and local stock movement behavior. Tombstones remain soft deletes.
- Reserved barcode behavior remains exactly `DISC:`/`MANUAL:` with ordinal
  matching through the shared policy.

## Acceptance evidence

1. Direct writer tests cover façade parity, caller-transaction rollback,
   reserved-barcode rejection, tombstone idempotence and pending-stock
   preservation across canonical barcode changes.
2. The catalog-pull gate requires direct batch use of the extracted remote
   writer/core/types and rejects remote implementation ownership in the façade.
3. No schema or full-refresh reconciliation behavior changes in this slice.

## ARCH-003 result

E1 through E4 leave `ProductRepository` as a façade over focused read, local
write, remote product identity and remote price collaborators. Future changes
must extend the relevant collaborator rather than reintroduce repository-owned
SQL into the façade.
