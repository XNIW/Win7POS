# Win7POS security follow-up remediation

- Date: 2026-07-21
- Base commit: `1be70172cab56895f66389a99b4a6fa92352c7e2`
- Branch: `codex/security-followup-20260721-153558`
- Security session: `470af0ad-e4fa-4ae0-a4c2-267b20912443`
- Scan: `0207326e-0be9-4257-9319-6dc8102ffa70`

## Outcome

The eight reported security findings were remediated and the complete cumulative
diff was reviewed again. The follow-up review found and closed additional
fail-open or amplification edges in catalog metadata, receipts, authorization,
SQLite reads and system-role grants. Final review status is P0/P1/P2 open: zero.

No production database, production credential or remote staging mutation was
used. No physical print was submitted during this follow-up. The earlier Epson
TM-T60 matrix remains operator-confirmed at 6/6 slips with zero drawer calls;
physical Windows 7 validation remains `NOT_RUN_WIN7_PHYSICAL` because the host
used for this work is Windows 11.

## Remediation map

| Finding | Resolution | Principal executable evidence |
|---|---|---|
| Catalog exactness and pagination could accept unsafe remote evidence | Remote catalog DTOs are bounded and semantically validated before mode selection, pagination evidence, staging, checkpoint or apply. Ambiguous/inconsistent full results remain fail-closed and cannot make a fresh database sale-safe. | `CatalogExactnessTests`, `RemoteCatalogBatchRepositoryTests`, catalog pull gate |
| Offline login could prime reusable authorization state before PIN success | Authorization is now a two-phase non-mutating preflight plus token-matched commit over authorization epoch and trusted generation. Wrong PIN, epoch change and generation change cannot prime the cache. | `--authorization-lease-smoke`, offline authorization gate |
| Trust/generation state could be written by stale work | Durable generation activation and mutation use exact generation/fingerprint checks and CAS fencing; stale completions and revoked generations are rejected. | `OnlineSyncGenerationRepositoryTests`, online client/security gates |
| A canonical-looking migration ledger could carry unsafe schema metadata | The ledger uses an exact canonical table definition, including indexes and triggers, and is revalidated before/after migrations and ledger writes. Malformed metadata fails closed. | `MigrationRunnerTests`, legacy migration gate |
| Receipt/product text could amplify memory or spooler work | A shared content policy bounds sale lines, names, barcodes, reasons, aggregate characters and UTF-8 bytes. Renderers, product ingress and repository sinks validate before allocation or mutation. Historical reads preflight SQL aggregates before materialization. | `ReceiptContentPolicyTests`, receipt and product gates |
| Supplier Excel apply could bypass or outlive its permission decision | Nullable permission composition is deny-all. Both raw and preview apply paths demand authorization before initialization, before backup and after backup immediately before mutation; backups are unique. Shipping smoke hooks were removed. | `.xlsx`/`.xls` supplier harness, security and supplier gates |
| Database backup/restore and maintenance actions relied on stale or optional checks | Maintenance callbacks are mandatory and deny by default. Restore reauthorizes after native file selection; backup, restore review and vacuum check again at action time. | security hardening and restore gates |
| Historical reprint could use a stale permission decision | Sales Register receives a required permission service and demands reprint authorization at the action boundary before spooler submission. | receipt/printer gates and lifecycle harness |

## Additional cumulative-diff corrections

- Catalog timestamps now require bounded `DateTimeOffset` semantics. Invalid
  incoming timestamps are rejected; valid remote evidence can repair malformed
  legacy ordering without a lexicographic fallback.
- Cursor, catalog version and heartbeat revision values reject controls,
  malformed surrogate pairs and oversized raw input before trimming or
  persistence. Unsafe legacy CAS evidence can still be atomically replaced.
- Catalog response validation now precedes diagnostics, lane evidence,
  pagination termination and all writes in both WPF and CLI paths.
- Receipt history uses a deferred SQLite read transaction, avoiding an
  unnecessary writer reservation. Direct sale-line insertion requires the
  supplied transaction to belong to the supplied connection, closing a future
  non-atomic cumulative-budget bypass.
- Cancelled operator switching now reloads the durable account and compares
  identity, role, active state, PIN-change state, discount limit, override bit
  and the exact permission set before retaining a cached session.
- System-role permission seeds are exact. Missing required grants are repaired,
  while an unexpected grant such as `security.override` on `cashier` fails
  closed on a no-op startup or restore candidate.
- Product editor `MaxLength` values and repository/service validation use the
  same receipt-safe name and barcode bounds, including programmatic callers.

## Final validation

- Required source gates: `33/33` PASS, including dialog standards `34/34`.
- Core/Data tests: `465/465` PASS, zero failed and zero skipped.
- Focused migration/restore security set: `40/40` PASS.
- Receipt-focused tests: `32/32` PASS.
- Release solution: PASS, zero warnings/errors.
- WPF and UI harness: Release `net48/x86` PASS, zero warnings/errors.
- CLI `--selftest --keepdb`: PASS, including sale/refund/daily/import paths.
- Authorization smoke: PASS for wrong PIN non-priming, epoch/generation changes,
  successful PIN commit, durable-authority drift and monotonic high-water;
  `hardwareEffects=0`.
- Supplier Excel smoke: `.xlsx` and `.xls` PASS; denial boundary, three-stage
  authorization lease, backup attribution and zero denied mutations verified.
- Receipt rendering: PASS for cash/card/split, line/cart discounts, EN/ES/IT/ZH,
  32/42 columns. Fiscal direct-print no-archive check: PASS.
- Lifecycle: PASS for 20 Daily/Sales/Printer/Recovery/Start-of-day cycles and 50
  display/manager cycles; zero residual windows, ViewModels or handlers.
- Release Pack completeness, Win7 runtime validation and Inno Setup installer:
  PASS. The pack excludes the CLI, UI harness, PDF runtime/output and secret-like
  files and contains the reviewed x86 closure.
- `git diff --check`: PASS. Added-line secret/personal-path scan: zero matches.

## Residual external validation

Software acceptance is complete. A real Windows 7 SP1 machine was not available,
so that platform-specific physical check is deliberately recorded as
`NOT_RUN_WIN7_PHYSICAL`, not PASS. The already confirmed Epson output is not
repeated because another physical submission would add no software coverage and
could produce duplicate fiscal-looking paper.
