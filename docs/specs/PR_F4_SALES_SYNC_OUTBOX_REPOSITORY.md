# PR-F4 Sales-sync outbox repository extraction

## Goal

Continue the staged `SaleRepository` decomposition by moving canonical
sales-sync outbox payload construction, queue reads and fenced state transitions
into `SalesSyncOutboxRepository`, while preserving every public
`SaleRepository` API and durable sync behavior.

## Included responsibilities

- Build and persist the canonical, immutable sales-sync payload and SHA-256 hash
  inside the caller-owned sale transaction.
- Read pending/stale queue work, summaries, drain state and unresolved status.
- Apply compare-and-swap claim, ACK, retry, dependency release, cancellation
  release and blocked transitions, including generation/claim-token fencing.
- Resolve remote product IDs for the canonical payload.

## Files

- `src/Win7POS.Data/Repositories/SaleRepository.cs` — public façade,
  client-sale-ID normalization/fallback and sale/reversal orchestration.
- `src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs` — F4 canonical
  enqueue, queue reads and state-transition implementation.
- `tests/Win7POS.Core.Tests/Data/SalesSyncOutboxRepositoryTests.cs` — direct
  repository/facade parity and transaction/CAS regressions.
- `scripts/check-sales-sync-outbox-repository-boundaries.ps1` — F4 ownership
  boundary gate, registered by `scripts/check-required-gates.ps1`.
- `scripts/sales-sync-outbox-gate-helpers.ps1` — shared structural C# method
  and Dapper-invocation extractor used by the F4 and dependent static gates.
- Existing sales-sync, outbox-binding, restore and status gates — updated to
  validate the F4 collaborator instead of stale façade SQL ownership.

## Invariants

- `SaleRepository` retains its public F4 signatures and delegates queue,
  summary, drain-state, remote-ID and CAS/fence operations to
  `SalesSyncOutboxRepository`. Each public façade overload is checked as its
  own structurally extracted method body (balanced braces or expression-body
  terminator): it forwards the exact argument order exactly once and cannot
  retain direct outbox Dapper, SQL, payload-building or
  connection/transaction lifecycle work.
- `EnqueueSalesSyncOutboxAsync` retains the existing blank-client-ID fallback
  and trim behavior, then passes the caller's `SqliteConnection` and
  `SqliteTransaction` to `EnqueueAsync`. The writer never opens, commits or
  rolls back that enqueue transaction. Every enqueue DB read/write, official
  binding lookup and reversal-economics snapshot uses that exact caller
  transaction.
- Canonical payload construction, official-shop binding, payload hash and
  idempotency conflict checks remain transactional and immutable. Inline
  reversal-economics computation is retained only to construct the canonical
  payload; standalone reversal boundary/dependency business APIs stay outside
  F4 for the later reversal slice.
- Pending reads retain the 1..50 bound and expose stale `in_progress` entries
  using the existing lease. A null historical batch/payload/hash snapshot
  remains claimable only by the exact null-aware CAS predicate.
- ACK, retry, dependency release, cancellation release and blocked transitions
  retain their expected-attempt CAS rules, sale status updates, generation
  identity and claim-token fence checks. Origin-block retains its separate
  status/attempt/lease/sale-ID CAS and updates both the outbox and sale state.
  Each terminal transition binds both its outbox and `sales` `ExecuteAsync`
  mutations to the local transaction, and makes the sales mutation reachable
  only through the local `rows == 1` CAS decision.

## Risks and negative cases

- A direct enqueue must observe caller-owned uncommitted sale/header/line data,
  and a caller rollback must leave no outbox row or client-ID mutation.
- Direct repository and façade execution both prove that an uncommitted header
  and line are serialized into the canonical payload through the caller's
  transaction, then that rollback removes the header, line and outbox row.
- Later sale/line mutation must not alter the persisted payload or hash; a
  tampered payload snapshot must fail its claim CAS.
- A caller-supplied pending take cannot bypass the 50-row cap; fresh leases
  must remain hidden while stale in-progress rows remain eligible.
- Null legacy snapshots, stale CAS reads, duplicate transitions, mismatched
  claim tokens and inactive generations must fail without advancing state.
- Product-ID lookup preserves mapped values while excluding unmapped,
  whitespace-only and duplicate IDs. Tests deliberately make an outbox ID
  differ from its sale ID so swapped forwarding cannot pass accidentally.
- A committed blank client-sale ID persists the canonical
  `win7pos-sale-{saleId}` fallback in both `sales` and the outbox binding.
- The extraction must not absorb stock, sale-safe, sale-header/line or
  standalone reversal-policy ownership.

## Benchmark

Not applicable as a direct performance change: F4 preserves existing SQL,
lease and transaction shapes. The canonical `batch-paged-full 19763 3 1000`
catalog benchmark remains a repository non-regression check alongside focused
tests and x86 smokes.

## Acceptance evidence

The direct repository/facade suite covers caller rollback, committed blank-ID
fallback persistence, canonical payload/hash immutability, bounded pending
reads, mapped/unmapped/duplicate remote IDs, stale lease eligibility,
null-aware claim CAS, origin-block CAS, retry/dependency/cancellation/blocked
transitions, and ACK plus non-ACK generation/claim-token fencing. The boundary
gate enforces per-overload façade delegation, exhaustive enqueue transaction
propagation, separately scoped prepare/ACK/retry/release-defer/blocked/origin
CAS predicates, local-transaction Dapper mutation binding, stale lease behavior,
excluded F5 reversal boundaries and all named regressions. Named test evidence
is extracted from the structural test body and must contain its own behavioral
markers, rather than merely matching a method declaration. Existing sales-sync,
outbox-binding, restore and status gates share the same structural extractor and
also require exact forwarding order in the relevant façade body.
