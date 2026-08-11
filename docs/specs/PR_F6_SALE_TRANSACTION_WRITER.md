# PR-F6 Sale transaction writer extraction

## Problem

After the F1--F5 extractions, `SaleRepository` still owns complete sale and
refund persistence transactions.  That remaining write orchestration combines
header/line storage, client-ID normalization, stock movement, outbox enqueue,
audit/void marking, and transaction lifecycle.

## Scope

Create internal `SaleTransactionWriter` as the owner of full sale/refund write
orchestration, header/line persistence, client-ID persistence, and PDF printed
updates.  `SaleRepository` retains its public APIs as exact facades plus F1--F5
collaborators and all existing public types.

## Invariants

- Receipt-policy validation occurs before SQLite access; sale-safe validation
  occurs inside the transaction before header, stock, or outbox work.
- Full-sale order remains header, client ID, lines, stock, outbox, commit.
- Refund/void order remains reversal validation, header, client ID,
  lines/budget, stock, outbox, audit/void mark, commit.
- Caller-owned line/refund and client-ID paths use the supplied connection and
  transaction; they never open, commit, or roll back a transaction.
- The writer receives the existing stock, reversal, and outbox collaborators;
  it never reconstructs `SaleRepository`.
- F4 immutable payload construction remains inside `SalesSyncOutboxRepository`
  on the caller transaction, so uncommitted header/line/reversal data remains
  visible.
- No public API, migration, payload/hash, client-ID fallback, or stock/outbox
  semantics changes.

## Risks and negative cases

- Failure after header/line/stock work must roll back every local mutation,
  including outbox and audit/void effects.
- Caller-owned line insertion must preserve budget, connection, and transaction
  validation behavior.
- The legacy refund overload and PDF-print update must preserve their narrow
  existing effects.
- The extraction must not pull reads, F3/F4/F5 ownership, or report behavior
  into the writer.

## Acceptance evidence

- Direct-writer/facade parity covers sale persistence, stock/outbox/hash,
  client IDs, caller-owned rollback paths, refund/void audit/mark behavior,
  legacy refund validation, and PDF printed behavior.
- Structural gates prove exact facades, transaction propagation, ordering, and
  ownership separation.
- Required gates, Core/Data suite, WPF net48/x86, UI smokes, CLI self-test,
  catalog benchmark, and full Gitleaks scans pass.
