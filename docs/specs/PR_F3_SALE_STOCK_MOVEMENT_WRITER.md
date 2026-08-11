# PR-F3 Sale stock movement writer extraction

## Goal

Continue the staged `SaleRepository` decomposition by moving local stock-ledger
and `product_meta` stock mutations into `SaleStockMovementWriter`, without
changing public sale APIs, movement keys, SQL semantics or transaction
ownership.

## Included responsibilities

- Record idempotent rows in `local_stock_movements` for sale, refund and void
  lines.
- Apply the corresponding local stock delta only after a newly inserted ledger
  row, preserving the non-negative stock clamp.
- Skip blank, zero-quantity, discount/tax and manual lines exactly as before.

## Files

- `src/Win7POS.Data/Repositories/SaleRepository.cs` — public façade,
  client-sale-id fallback and enclosing sale transaction orchestration.
- `src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs` — caller-owned
  local ledger and stock mutation implementation.
- `tests/Win7POS.Core.Tests/Data/SaleStockMovementWriterTests.cs` — direct
  writer/facade parity and transactional regressions.
- `scripts/check-sale-stock-movement-writer-boundaries.ps1` — F3 ownership
  boundary gate, registered by `scripts/check-required-gates.ps1`.

## Invariants

- `SaleRepository.ApplyLocalStockMovementsAsync` retains its public signature,
  returns for empty input before either client-ID fallback or delegation, and
  otherwise retains `EnsureClientSaleIdAsync` fallback before it delegates.
- The writer receives the caller's `SqliteConnection`, `SqliteTransaction` and
  resolved client-sale ID. It never opens a connection, begins, commits or
  rolls back a transaction; the façade also has no connection/transaction
  lifecycle ownership. Its exactly two `ExecuteAsync` writes both receive the
  caller transaction.
- Movement keys remain the raw resolved
  `clientSaleId:lineId:movementKind` (without a new trim); the persisted ledger
  remains `INSERT OR IGNORE` and a duplicate never applies stock twice.
- An unsaved line (`Id == 0`) continues to use `0` in the movement key and a
  nullable `sale_line_id` in the ledger row.
- Sale lines decrement by the absolute quantity, including an anomalous
  negative sale-line quantity; refund and void lines increase stock using
  `refund_increment` and `void_reverse` respectively.
- `product_meta.stock_qty` remains clamped at zero. A missing metadata row is
  still ledger-only rather than an implicit product/meta creation.
- Sale headers, sale-line persistence, reversal validation and sync-outbox
  work remain in `SaleRepository` and outside the writer.

## Risks and negative cases

- Reserved economic/manual lines, blank barcodes and zero quantities must not
  create a movement or alter stock.
- Replaying the same line must retain one ledger row and one stock delta, even
  when a sale would otherwise drive stock below zero.
- Direct writer calls must see caller-owned uncommitted data and leave no
  ledger or stock mutation if the caller rolls back.
- The façade must preserve the same caller-owned rollback behavior; it must not
  commit the ledger or stock update itself.
- Empty lines with a blank `ClientSaleId` must remain a no-op: no fallback
  header write, no writer delegation and no ledger/stock mutation.
- A blank façade `ClientSaleId` must be persisted through the existing fallback
  before its movement key is built.

## Benchmark

Not applicable as a direct performance change: this extraction preserves the
existing two write statements and caller transaction shape. The canonical
`batch-paged-full 19763 3 1000` catalog benchmark remains a repository
non-regression check alongside focused tests and x86 smokes.

## Acceptance evidence

The direct writer tests compare persisted stock and all ledger fields against
the public façade, cover skip rules, duplicate/idempotent clamp behavior,
missing-metadata ledger-only behavior, refund/void mappings, caller rollback
and blank-client-ID fallback. It also proves a whitespace-padded nonblank
client ID is passed through unchanged and an unsaved line keeps a null
`sale_line_id`; it exercises negative ordinary-sale quantities, façade rollback
and the empty-input/blank-client-ID no-op. The static gate asserts the
façade/writer split, the early-return ordering, bounded transaction
propagation for each of the writer's exactly two `ExecuteAsync` calls, the only
allowed orchestration call sites, exclusion of sale-header, line and outbox
ownership, resource-lifecycle ownership, and `[TestMethod]` declarations for
each named regression.
