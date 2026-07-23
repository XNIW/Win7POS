# ARCH-005 — Catalog sale-safety policy extraction

## Problem

`CatalogShopStateRepository` currently both reads SQLite state and owns the
branching policy that determines whether a sale is safe.  That makes the
fail-closed decision hard to exercise independently of a database and keeps
domain policy in the Data layer.

## Scope

Extract only the read-side decision into a pure `Win7POS.Core` policy.  Keep
the repository as the single-query, transaction-owning adapter and retain its
public `CatalogSaleSafetyEvaluation` contract.  `RequireExactnessSaleSafetyAsync`
is intentionally outside this slice because it is a write precondition with a
separate exception contract.

## Invariants

- The exact reason codes and fail-closed outcomes remain unchanged.
- An unbound legacy database is sale-safe only when `allowLegacyUnbound` is
  true; official readiness remains blocked.
- Shop code comparison is ordinal after invariant upper-case normalization;
  non-empty shop identifiers compare ordinal-ignore-case.
- Exactness accepts precisely the existing case-insensitive enum parsing
  semantics and only `Verified` is safe.
- The ordinary-sale path issues no additional connection, query, transaction,
  commit, or write before stock and outbox writes.
- Core stays independent from Dapper, SQLite, and Data.

## Acceptance criteria

1. A Core policy consumes an immutable snapshot and returns an immutable
   decision.
2. The Data adapter reads the same nine settings in the same transaction and
   maps the Core decision back to `CatalogSaleSafetyEvaluation`.
3. Tests cover legacy behavior, partial/mismatched bindings, invalid/required
   repair state, missing/unverified/mismatched exactness, and a verified safe
   state.
4. Existing ordinary-sale transactional barrier tests continue to pass.
5. Architecture, required-gate, Core/Data, CLI self-test, and WPF net48/x86
   validation stay green.

## Files

- `src/Win7POS.Core/Online/CatalogSaleSafetyPolicy.cs`
- `src/Win7POS.Data/Online/CatalogShopStateRepository.cs`
- `tests/Win7POS.Core.Tests/Online/CatalogSaleSafetyPolicyTests.cs`
- `tests/Win7POS.Core.Tests/Data/SaleSafetyBarrierTests.cs` (existing
  integration coverage retained)

## Risks and negative cases

The primary risk is a superficially equivalent refactor changing a reason
code, normalization rule, enum parsing behavior, or legacy-unbound decision.
Table-driven policy tests lock those cases while the database tests preserve
the transaction boundary and rollback behavior.

## Benchmark and validation

This is a zero-I/O policy extraction: the adapter retains one settings query
and creates no additional SQLite work.  Validate with focused policy and
barrier tests, the full Core/Data suite, required gates, CLI self-test, WPF
Release x86 build, dialog standards, and `git diff --check`.
