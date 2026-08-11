# PR-F1 Sale read repository extraction

## Goal

Start the staged `SaleRepository` decomposition by extracting its pure sale
reads and reporting queries into `SaleReadRepository`, while preserving the
public façade and leaving all mutation-sensitive work untouched.

## Included reads

- recent sales and range/date/code queries;
- daily and hourly reporting;
- sale lookup with receipt-shop-snapshot validation.

## Invariants

- `SaleRepository` retains every public F1 signature and delegates to the read
  collaborator without caller rewrites.
- Date boundaries, operator filtering, ordering, fiscal compatibility
  parameters and local-time reporting SQL are behaviorally unchanged.
- The reader uses only short-lived read connections. It does not own a
  transaction, sale lines, line budgets, reversal mutation, stock mutation or
  sales-sync outbox work.
- Receipt snapshot length/validity checks remain fail-closed on direct reader
  and façade paths.

## Deferred slices

- F2: line/budget and receipt-surface reads.
- F3: stock movement ownership.
- F4: sync outbox ownership.
- F5: reversal and reversal-economics ownership.
- F6: final transaction/header/line write boundary.

## Acceptance evidence

Direct reader tests cover all-method façade parity, midnight/range boundaries,
operator/fiscal compatibility, invalid inputs, reversal snapshots, oversized
snapshot rejection and parallel read immutability. The sale-boundary gate
asserts the exact F1 surface and excludes all deferred mutation ownership.
