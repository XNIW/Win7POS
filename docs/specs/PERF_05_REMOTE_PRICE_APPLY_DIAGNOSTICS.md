# PERF-05 Remote price-apply diagnostics

## Scope

Expose a run-level, observational diagnostic at
`CatalogApplyRunDiagnostics.RemotePriceApply`.  Its public
`RemotePriceApplyDiagnostics` value exposes:

- `SqlCommandCount`
- `SqlStatementCount`

The counters cover only remote-price apply work.  They do not replace the
existing run-context diagnostics and do not alter catalog, price, ownership,
queue, retry, or transaction behavior.

## Contract

- `ApplyWithinRunAsync` creates a fresh diagnostic accumulator for every page.
- Remote-price helpers record the SQL commands and SQL statements they execute
  into that page-local accumulator; the ownership write is one command and two
  statements.
- The page-local values are merged into `RemotePriceApply` only after that
  page's `tx.Commit()` succeeds.
- A rolled-back or otherwise failed page publishes no remote-price counters.
- The diagnostics are aggregate evidence, not a latency, throughput, or
  release threshold.

## Deterministic baseline

For three valid remote-price rows across two committed price-only pages, the
expected aggregate is **19 SQL commands** and **22 SQL statements**:

`commands = 5N + 2P`; `statements = 6N + 2P`, with `N = 3` and `P = 2`.

This is an exact functional accounting assertion for that controlled input,
not a performance budget.  Failed-page tests must prove that this accounting
is not published on rollback.  The `batch-price-only` benchmark reports the
same evidence but imposes no pass/fail timing or allocation threshold.

## Acceptance evidence

- `PriceOnlyPagesPublishExactRemotePriceApplyDiagnostics` proves the 19/22
  baseline.
- `FailedPricePageDoesNotPublishRemotePriceApplyDiagnostics` proves
  post-commit publication only.
- The PERF-05 static gate protects the public contract, page-local flow,
  optional helper instrumentation, benchmark markers, and the two tests.
