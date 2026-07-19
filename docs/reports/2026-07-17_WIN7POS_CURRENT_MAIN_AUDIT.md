# Historical Win7POS current-main audit and original PR-B migration design

> Historical snapshot: “current main” below means
> `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c` as audited on 2026-07-17. It
> predates PR #7’s normal merge `db623a5bf61c026662fe967b905b62940bec52e9`
> and the post-PR7 PR-B refresh/0007. Do not use the six-migration, six-fixture,
> P1, external-backlog or validation counts below as current evidence; see the
> PR-B refresh closeout and authoritative external backlog.

## Provenance and scope

- Audited main: `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c`.
- PR-B branch: `codex/pr-b-versioned-migrations-20260717-143330`.
- Structural scope: versioned, checksummed, idempotent SQLite migrations only.
- Read-only audit lanes: legacy schema/migrations, hardware/settings, sync and
  performance, CI/release/privacy, plus an independent diff review.
- Writer model: one parent writer; four read-only subprocesses, no recursive
  agents, credentials, branch operations or file changes by subprocesses.

Frozen in PR-B: hardware runtime, printer/settings dialogs, customer display,
scanner input, sync policy/coordinator/scheduler, HTTP/sync services, catalog and
sale repositories, packages, payload/hash/idempotency, reversal economics and
WAL/journal policy.

## Current schema inventory

Before PR-B, `DbInitializer` used one monolithic transaction with no immutable
ledger or checksum verification.

| Item | Count | Inventory |
| --- | ---: | --- |
| Application tables | 22 | 15 core/security plus 7 sync/catalog evidence tables |
| Legacy additive column checks | 90 | `EnsureColumn` calls across users, sales, lines, catalog and outboxes |
| Canonical named indexes | 37 | uniqueness, lookup, outbox due-order, catalog ownership and active-row access |
| Declared foreign keys | 5 | sale lines, held-cart lines, users, role permissions and sales outbox |
| Historical backfill families | 3 | ownership evidence, outbox schema/operation, shop-origin fail-closed normalization |

Core/security tables:

`products`, `sales`, `sale_lines`, `app_settings`, `audit_log`, `suppliers`,
`categories`, `product_meta`, `product_price_history`, `held_carts`,
`held_cart_lines`, `roles`, `users`, `role_permissions`, `security_events`.

Sync/catalog tables:

`local_stock_movements`, `sales_sync_outbox`, `catalog_import_outbox`,
`remote_catalog_pending_prices`, `remote_catalog_product_references`,
`remote_catalog_price_ownership`, `remote_catalog_price_evidence_quarantine`.

PR-B adds only `schema_migrations`. Default values and nullability remain sourced
from the existing canonical DDL/`EnsureColumn` definitions; no table rebuild,
column removal or hard delete is introduced.

## Immutable migration registry

| Order | Migration ID | Description | Minimum app | Rollback compatibility | Backup |
| ---: | --- | --- | --- | --- | --- |
| 1 | `0001-core-pos-schema` | Core POS, catalog, cart and security tables | `1.0.0` | Additive; older readers ignore unused tables/columns. | Yes |
| 2 | `0002-supported-legacy-columns` | Security, reversal, sync and remote-catalog columns | `1.0.0` | Additive columns; no automatic downgrade. | Yes |
| 3 | `0003-outbox-catalog-evidence` | Outboxes and catalog exactness/ownership evidence | `1.0.0` | Additive; ambiguous history remains fail closed. | Yes |
| 4 | `0004-shop-bound-outbox-backfill` | Schema/operation normalization and conservative shop binding | `1.0.0` | Forward-only classification; payload/hash remain immutable. | Yes |
| 5 | `0005-canonical-query-indexes` | 37 canonical indexes | `1.0.0` | Additive indexes; uniqueness is not downgraded. | Yes |
| 6 | `0006-system-role-permissions` | System roles and required permission grants | `1.0.0` | Additive grants; no default user/credential. | Yes |

Each ID uses a stable semantic four-digit prefix. The checksum is lowercase
SHA-256 of normalized canonical material describing exact tables, columns,
indexes, backfill and invariants. Once published, ID, checksum material and
description are immutable.

## Ledger and startup rules

`schema_migrations` contains:

```sql
migration_id TEXT PRIMARY KEY,
checksum TEXT NOT NULL,
description TEXT NOT NULL,
applied_at TEXT NOT NULL,
app_version TEXT NULL
```

- New DB: create an empty ledger, apply all migrations in order.
- Existing DB without a ledger: inspect actual `sqlite_master`, `table_info`,
  `foreign_key_list`, index names and data invariants; bootstrap only the
  contiguous satisfied prefix.
- Existing DB: create and verify an online backup before ledger bootstrap or the
  first pending backup-required migration.
- Log only the generated backup filename.
- Applied checksum/description mismatch blocks startup.
- A gap or unknown future ID blocks startup/downgrade.
- Every migration uses an explicit immediate transaction containing DDL,
  backfill, declared invariant validation, `integrity_check`,
  `foreign_key_check`, and its ledger insert.
- Failed migration rolls back its schema, data and ledger row.
- Applied migrations are never replayed. Mutable outbox/system-role invariants
  remain a separate repeatable reconciler on no-op startup.
- The existing process-wide maintenance fence serializes concurrent startup;
  no WAL change is made.

## Sanitized legacy support matrix

| Fixture | Represents | Required outcome |
| --- | --- | --- |
| `legacy_initial_minimal.sql` | First minimal sales schema | Upgrade all missing generations and preserve product/sale/line evidence. |
| `legacy_pre_refund_void.sql` | Settings before reversal support | Preserve settings and add reversal/sync columns. |
| `legacy_pre_outbox.sql` | Security/catalog before durable outboxes | Preserve user/role/catalog/cart data. |
| `legacy_pre_shop_binding.sql` | Outboxes without authoritative shop columns | Preserve payload/hash and block ambiguous active rows with `legacy_origin_ambiguous`. |
| `legacy_pre_catalog_exactness.sql` | Shop-bound outboxes before exactness ownership | Backfill only unambiguous pending ownership. |
| `legacy_current_main_unversioned.sql` | Immediate pre-PR-B main schema | Bootstrap all six rows without replaying DDL/backfill. |

Every fixture is synthetic SQL only. Tests compare upgraded schema semantics to
a fresh DB, verify all canonical indexes/FKs, preserve probes and immutable
outbox evidence, validate integrity/FKs, and require a second startup to leave
ledger timestamps and backup count unchanged.

## Regression coverage

- ordered/unique registry and 64-character checksums;
- new database and repeated no-op reopen;
- latest ledgerless bootstrap from real detection;
- two concurrent `EnsureCreated` calls;
- failure before DDL, after DDL and during backfill;
- applied migration never replayed;
- checksum tamper, ledger gap and unknown future migration;
- partially satisfied historical prefix;
- actual verified backup before ledger mutation;
- injected backup failure and invalid-FK source before ledger mutation;
- atomic restore of a pre-migration backup followed by upgrade to latest;
- six sanitized fixture generations.

The static gate `check-win7pos-legacy-db-migrations.ps1` performs no restore or
build and is included in the canonical required gates.

## Read-only audit findings

| Area | P0 | P1 | Decision |
| --- | ---: | ---: | --- |
| PR-B migration implementation | 0 | 0 | In scope and covered by fixtures, rollback, backup and concurrency tests. |
| Hardware/settings | 0 | 0 | Roadmap-level only; see `WIN7POS_HARDWARE_SETTINGS_ROADMAP.md`. |
| Sync/performance | 0 | 4 | Auth-stop, lane coupling, HTTP allocation and page×catalog scaling deferred. |
| Release/supply chain | 0 | 2 | Locked/signable chain and fail-closed local installer deferred. |

Repository-level open P1 total is `6`; PR-B-specific open P1 is `0`. The six
findings are documented in `WIN7POS_SYNC_EFFICIENCY_ROADMAP.md` and do not justify
changing frozen runtime surfaces in this PR.

External certification remains `OPEN 0/16`: authenticated staging, physical
Win7, dual monitor, scanner, Xprinter, drawer and DPI/language runtime evidence
cannot be replaced by static tests or synthetic databases.

## Local validation environment note

The isolated command sandbox denies the same-directory `File.Replace` used by
restore tests. The unmodified suite was therefore rerun in the normal Windows
host context and passed completely; the existing restore tests failed only in
the sandbox, confirming an environmental restriction rather than a product
defect. The NuGet vulnerability service was unreachable from this host, so the
solution build reports four `NU1900` audit warnings and zero compiler errors.
No package, source, security policy or audit setting was changed to mask either
environmental condition. GitHub CI remains the independent exact-head authority.

## Validation snapshot

| Check | Result |
| --- | --- |
| Canonical gates | `31/31 PASS` locally |
| PR-B migration gate | `PASS` locally; static/no implicit build |
| Automated tests | `290/290 PASS`, skipped `0` |
| Solution | `PASS`, 0 compiler errors; 4 external `NU1900` audit warnings |
| WPF `net48/x86` | `PASS`, 0 warnings / 0 errors |
| CLI selftest | `PASS` (`自检 PASS`) |
| 2,000-row benchmark | `PASS`; batch median `262.188 ms`, `77.21x` vs legacy |
| 19,762-row benchmark | `PASS`; exactness `Verified` in 3/3 iterations |
| Release pack / installer | `PASS` on a clean committed head; completeness and Win7 runtime validators `PASS`, Inno Setup 6.7.3 installer generated |

## Recommendation

PR-B is structurally ready for review after clean-head release validation and
the GitHub CI, performance and release-pack workflows succeed on the exact PR
head. No automatic merge is authorized.

## 2026-07-19 refresh after PR #7

- PR #7 was normally merged as
  `db623a5bf61c026662fe967b905b62940bec52e9`; its exact-head and post-merge CI
  and Release Pack runs passed.
- PR-B was refreshed by a normal merge of that main, without rebase, squash or
  force push. Receipt/recovery/hardware behavior from PR #7 is preserved.
- `receipt_shop_snapshot` is intentionally absent from the immutable materials
  and checksum pins for 0001–0006. New migration
  `0007-receipt-shop-snapshot` owns the additive `TEXT NULL` column.
- The legacy inventory is now 91 additive column effects including 0007. A
  fresh or upgraded latest database contains the same application tables as
  post-PR7 main plus `schema_migrations`; no payload/hash/idempotency, reversal
  economics, sync policy or WAL/journal-mode change is introduced.
- Exact post-PR7 ledgerless databases are recognized only when the full frozen
  structure and prior migration data invariants match; they bootstrap 0001–0007
  with null application versions. Pre-PR7 databases bootstrap only their
  satisfied prefix and apply 0007 normally after a verified backup.
- Current validation counts and exact refreshed head are recorded in the PR-B
  closeout entry after the final local and GitHub runs; the `31/31` and `290/290`
  values above remain the historical 2026-07-17 snapshot.
