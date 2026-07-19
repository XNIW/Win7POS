# PR #6 migration refresh after PR #7

## Provenance and integration method

- PR #6 branch: `codex/pr-b-versioned-migrations-20260717-143330`.
- Published pre-refresh head: `4499a67fd9df4222c5311577aa4beb8a71eeca1c`.
- New main merged into the branch: `db623a5bf61c026662fe967b905b62940bec52e9`.
- Preservation branch: `backup/pre-pr6-refresh-20260719-114906`.
- Verified all-ref bundle:
  `C:\Dev\Win7POS-pre-pr6-refresh-20260719-114906.bundle`.
- Integration used a normal `git merge --no-ff origin/main`. No rebase, squash,
  force push or published-history rewrite was used.

## Immutable migration resolution

The published IDs, descriptions, materials and checksum pins for 0001–0006 are
unchanged. In particular, `receipt_shop_snapshot` is absent from
`CoreSchemaSql`, `SupportedLegacyColumns` and `PrBKnownSchemaSql`, because those
values feed already-published checksum material.

PR #7's additive column is owned by the appended migration:

- ID: `0007-receipt-shop-snapshot`;
- column: `sales.receipt_shop_snapshot TEXT NULL`;
- checksum: `a1d12cca8bbfeb57872ee854e18cc32bf98258937d1f7be4be91d925f2ef6462`;
- backup required: yes;
- rollback rule: restore the verified pre-migration backup.

A bounded post-PR7 database without a ledger bootstraps all seven historically
satisfied rows with null `app_version`. A pre-PR7 database bootstraps only its
contiguous satisfied prefix and applies 0007 normally. Special baseline
recognition is available only to the exact canonical registry; custom
predecessors cannot be falsely ledgered.

Current-schema validation runs before mutable outbox/role reconciliation.
Migration 0007 validates the complete supported latest structure in its own
transaction, so malformed prior schema rolls back both the added column and its
ledger row. Restore validation rejects an authentic latest ledger whose current
schema is missing the receipt column and reinstates the original live database.

## Regression coverage

- Seven sanitized legacy generations, including a post-PR7 ledgerless fixture
  with a Unicode receipt snapshot.
- Published checksum pins, fresh/reopen, contiguous prefix and 0006→0007 upgrade.
- Post-PR7 bootstrap, unknown column, missing ownership backfill and custom
  predecessor fail-closed cases.
- Authentic latest ledger with missing current schema, validation-before-
  reconciliation and malformed 0006-ledger rollback.
- Verified pre-migration backup, backup failure, restore/re-upgrade and invalid
  candidate rollback with receipt snapshot and ledger preservation.
- Immutable outbox payload/hash evidence and reversal/domain rows remain covered
  by the full fixture suite.

## Scope audit

- No sync policy, scheduler, HTTP contract or repository paging change.
- No payload, hash, idempotency, sale economics or refund/void change.
- No WAL or journal-mode change.
- PR #7 receipt, recovery, release and hardware changes are preserved.
- The Epson TM-T60 physical result was not rerun: the owner-confirmed six-job
  Windows 11 evidence remains PASS; physical Windows 7 remains
  `NOT_RUN_WIN7_PHYSICAL`.

An inherited restore-hardening P2 remains outside PR #6: production restore
currently relies on the maintenance fence, verified online backup and pooled
connection close to materialize WAL state before replacement, while the
declared verified rollback path is not the atomic `.old` source. A separate
restore PR should exercise persistent/crash WAL and use the verified rollback
explicitly. `AtomicRestoreInstaller` is byte-identical across the PR #6 base,
published head and refreshed main, so this is not a PR #6 regression.

## Local validation before publication

- Canonical source gates: `33/33 PASS`.
- Dialog standards: `34/34 PASS`.
- Migration/fixture/restore focus: `38/38 PASS`.
- Full Core/Data suite: `336/336 PASS`, skipped `0`.
- Solution Release build: zero warnings and zero errors.
- WPF `net48/x86`: zero warnings and zero errors.
- CLI selftest: `自检 PASS`.
- 2,000-row benchmark: legacy median `18,436.841 ms`, batch median
  `609.510 ms`, `30.25x`; products/prices exact and pending prices zero.
- 19,762-row paged full benchmark: `Verified` in 3/3 runs, pending prices zero;
  median `5,488.546 ms`.

Clean committed-head Release Pack, installer and exact-head GitHub runs are the
remaining publication gates. Merge authorization requires all of them green and
P0/P1 open at zero.
