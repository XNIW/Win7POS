# PR-F2 Sale line read repository extraction

## Goal

Continue the staged `SaleRepository` decomposition by placing receipt-line reads
and stored-line budget inspection in `SaleLineReadRepository`, without changing
the public façade or the transaction boundary of sale writes.

## Included responsibilities

- Read a sale's persisted lines in their existing `id ASC` order.
- Validate persisted line budgets before materializing receipt content.
- Provide the same budget inspection to the existing caller-owned line writer.

## Files

- `src/Win7POS.Data/Repositories/SaleRepository.cs` — public façade delegation
  and existing line-write orchestration.
- `src/Win7POS.Data/Repositories/SaleLineReadRepository.cs` — deferred
  persisted-line reader and caller-transaction budget guard.
- `tests/Win7POS.Core.Tests/Data/SaleLineReadRepositoryTests.cs` — direct
  reader/facade parity and transaction regressions.
- `scripts/check-sale-line-read-repository-boundaries.ps1` — F2 ownership
  boundary gate, registered by `scripts/check-required-gates.ps1`.

## Invariants

- `SaleRepository.GetLinesBySaleIdAsync` retains its public signature and
  delegates to the reader.
- Direct reads retain the existing deferred read transaction: validate aggregate
  limits, materialize lines, validate individual content, then commit.
- The shared budget helper receives the caller's connection and transaction;
  it never creates, commits or rolls back a transaction.
- `InsertSaleLinesAsync` remains the owner of argument validation, cumulative
  budget checks and line inserts.
- Reversal reads, stock movement, outbox work, sale headers and line writes
  remain outside this slice.

## Risks and negative cases

- Historical content must fail before row materialization for oversized names
  and barcodes, excessive row counts, aggregate character limits and aggregate
  UTF-8-byte limits.
- The direct read must retain one deferred transaction through budget preflight,
  ordered materialization, content validation and commit.
- The shared guard must observe uncommitted caller data but never commit or
  roll back the caller transaction.
- Parallel reads must leave persisted sales-line state unchanged.

## Benchmark

Not applicable as a direct performance change: this is an ownership extraction
that retains the same SQL, ordering and transaction shape. The canonical
`batch-paged-full 19763 3 1000` catalog benchmark is still run as a repository
non-regression check alongside the focused tests and x86 smokes.

## Acceptance evidence

Direct-reader tests compare full ordered line results with the façade, cover
empty results, oversized name/barcode, aggregate-character, aggregate-UTF-8
and line-count historical failures, and demonstrate that parallel reads do not
mutate persisted state. The caller-transaction test proves the shared guard
does not own completion. Existing writer tests retain coverage for cumulative
budgets and transaction behavior. The sale-boundary gates assert the exact F2
ownership split and transaction propagation.
