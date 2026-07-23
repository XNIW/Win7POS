# PR-F5 Sale reversal writer extraction

## Problem

`SaleRepository` currently owns sale creation, stock, header/line persistence,
sales-sync outbox work, and reversal/dependency policy.  The reversal slice has
enough independent read and caller-owned transaction behavior to be extracted
without changing public callers.

## Scope

Create the internal `SaleReversalWriter` collaborator and move reversal reads,
economics/dependency evaluation, caller-owned boundary validation, and the
caller-owned void mark into it.  `SaleRepository` keeps its public and internal
facades, sale/refund insertion orchestration, header/line persistence, stock,
outbox, client-ID normalization, and audit behavior.

## Invariants

- Public `ReversalDependencyState`, `ReversalDependencyDecision`, and
  `SaleLineReturnableDto` remain in their current namespace and API shape.
- Facades preserve argument order, return values, domain error codes, and
  connection/transaction ownership.
- `ValidateReversalBoundaryAsync(conn, tx, sale, lines)` and
  `MarkSaleVoidedAsync(conn, tx, ...)` use the supplied transaction and never
  open, commit, or roll back a transaction.
- Inline reversal-economics construction inside
  `SalesSyncOutboxRepository.EnqueueAsync` remains there: it must read an
  uncommitted reversal in the caller transaction when building the immutable
  sync payload.
- The writer does not acquire sale-header/line, stock, outbox, or audit
  ownership.

## Risks and negative cases

- Reversal validation must see uncommitted caller data and leave no mutation
  after caller rollback.
- Void marking must remain atomic with the refund/void orchestration.
- Dependency and persisted-economics outcomes must remain fail-closed for
  legacy, mismatched-shop, partial, discounted, and taxed reversals.
- No new connection or transaction may be introduced in caller-owned methods.

## Acceptance evidence

- Direct-writer and `SaleRepository` facade tests cover reversal reads,
  economics/dependency outcomes, caller-owned rollback/visibility, and void
  marking.
- Existing integration coverage for ACK wait, permanent block, shop mismatch,
  partial/refund/void economics, and legacy payload failure remains green.
- Structural gates prove façade delegation, writer ownership boundaries, and
  caller transaction propagation.
- Required gates, Core/Data suite, WPF net48/x86, UI smokes, CLI self-test,
  and the catalog non-regression benchmark pass.
