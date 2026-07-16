# ASUS-W7POS-013 — Catalog exactness evidence

- Date: 2026-07-14
- Branch: `integration/asus-catalog-ui-runtime-20260714`
- Closure software commit: `bb4178b506afe5ebc86313f010e2271ca8075f84`

## Outcome

The integrated client has a backward-compatible optional `catalogSummary` contract and explicit `Verified`, `Unverified`, and `Mismatch` states. A full refresh cannot become verified or sale-safe until its final page is drained, shop/version/summary evidence stays stable, all accepted rows commit, authoritative counts and identities agree, and the SQLite structural audit is clean.

The contract, compatibility checks, repository invariants, pull-service wiring, repair policy, UI diagnostics, and local regression tests pass. The real Admin Console-versus-staging comparison remains `BLOCKED_EXTERNAL`: no staging credentials or authenticated Admin Console session were available. The value 19,762 is only the expected product count supplied in the task brief; it was not observed in Admin Console or an authenticated staging database during this run.

## Task ledger

| Task | State | Commit | Evidence | Notes |
|---|---|---|---|---|
| ASUS-W7POS-013.1 optional summary contract | DONE | `bb4178b` | contract round-trip and malformed-summary tests; catalog pull checker | No staging count is embedded in production code |
| ASUS-W7POS-013.2 full/delta exactness audit | DONE | `bb4178b` | local exactness and safety tests | Includes duplicate, invalid, metadata, reference-map, orphan, pending-price, tombstone, and non-authoritative-active invariants |
| ASUS-W7POS-013.3 pull-service fail-closed integration | DONE | `bb4178b` | service policy tests and checker | Skipped rows, unstable pages, mismatches, and unsafe final audits prevent cursor/sale-safe commit |
| ASUS-W7POS-013.4 persistence, repair, and UI diagnostics | DONE | `bb4178b` | shop-bound repository tests; sync-status UI checker | Authorized explicit repair clears cursor/sale-safe and reruns a controlled full refresh |
| ASUS-W7POS-013.5 Admin Console versus staging SQLite proof | BLOCKED_EXTERNAL | — | `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\staging-admin-login-blocked.png` | Authentication is required to capture authoritative products/categories/suppliers/prices and run the bound staging audit |

## Contract and verification semantics

Optional response field:

```text
catalogSummary {
  products,
  activeProducts,
  categories,
  suppliers,
  prices,
  checksum,
  checksumAlgorithm
}
```

- Counts are nullable so omission by an older server remains distinguishable from authoritative zero.
- Negative values, `activeProducts > products`, malformed checksum metadata, an algorithm without a checksum, and unsupported algorithms are rejected by compatibility validation.
- When a summary is supplied, all required counts must be present before exact verification is possible.
- Products, categories, suppliers, and prices retain raw authoritative ID evidence so empty or duplicate identities cannot be hidden by a deduplicating collection.
- Duplicate product, category, or supplier remote IDs are rejected within each page before apply.
- `hasMore=true`, a cursor cycle, page/version/summary drift, skipped rows, duplicate/invalid IDs, missing `product_meta`, invalid product data, invalid category/supplier mappings, orphan/inactive references, pending prices, or non-authoritative active rows cannot produce `Verified`.
- A completed legacy response without `catalogSummary` remains compatible but is `Unverified`; it is never presented as exactly verified.

### Checksum semantics

The compatibility layer accepts only a checksum/algorithm pair. The currently supported format is SHA-256 with a 64-hex-character digest; algorithm and hex digest comparison are case-insensitive.

Admin Web has not defined a canonical byte representation of the catalog, so the client cannot compute the actual digest from runtime rows. Consequently, when the server supplies a checksum, the integrated runtime records it but evaluates completeness as `Unverified`, not `Verified`. A checksum mismatch can be called `Mismatch` only when both expected and locally computed digests are genuinely comparable under the same defined canonicalization. No such staging comparison was available here.

Without a supplied checksum, a complete summary may be `Verified` on authoritative counts, identities, final-page evidence, and structural SQLite invariants. The UI describes this state as verified on counts and identities rather than implying checksum proof.

## SQLite invariants

The final audit covers at least:

- active remote products and distinct trimmed `remote_product_id` values;
- duplicate remote product IDs and duplicate active barcodes;
- invalid names, barcodes, prices, and missing `product_meta`;
- active/distinct remote categories and suppliers;
- orphan or inactive category/supplier references;
- products missing the persistent remote-reference map;
- invalid category/supplier mappings in that map;
- pending remote prices and conflicting remote price IDs;
- immutable remote-price ownership and append-only quarantine evidence for ambiguous legacy rows repaired by an authoritative full refresh;
- inactive/tombstoned rows;
- active rows absent from the authoritative full-refresh identity sets.

The persistent `remote_catalog_product_references` map allows products received before their category/supplier page to be relinked later. Delta relinking uses indexed temporary target tables and touches only products affected by the page; it does not rewrite all `product_meta` rows. Full reconciliation removes stale mapping rows and fails exactness if any active remote product remains unmapped or mapped to the wrong remote identity.

Remote price IDs have one immutable authoritative owner. Ordinary delta processing fails closed on ambiguous legacy evidence or same-owner payload drift. Only an authoritative full refresh may quarantine ambiguous legacy rows append-only, clear their remote ID while preserving history, and adopt the verified owner. Catalog-import ACK application is atomic; top-level `RemotePriceIds[]` takes precedence over the overlapping legacy `Items[].RemotePriceId`, including blank-type wildcard overlap.

## Pull, cursor, and repair policy

- A first bootstrap, missing/legacy cursor, rejected/expired cursor, server `full_refresh`, authorized shop change, restore, exactness mismatch, or explicit administrator repair starts a full refresh.
- Full refresh pins catalog version and summary across pages, rejects cursor cycles, and raises the page allowance sufficiently to drain the authoritative snapshot.
- Delta continuation checkpoints are persisted fail-closed and must be complete: shop binding, mode, cursor chain, catalog version, summary-presence pin, and up to 256 full fingerprints are validated without truncation. A pinned summary cannot disappear within or across runs.
- Catalog versions are limited to 128 characters and reject control characters or surrounding whitespace before apply or checkpoint persistence.
- The full cursor is persisted only after reconciliation, exactness evaluation, active-product safety, and sale-safe evidence succeed.
- A final delta performs the current SQLite structural audit. An unsafe delta requests repair rather than preserving a false verified state.
- A normal partial delta may persist its continuation cursor without replacing a previously valid sale-safe snapshot; it cannot claim a new full verification.
- Cursor binding, exactness evidence, sale-safe state, shop ID/code, and epoch are transactionally guarded; `repair_required` accepts only the persisted values `0` and `1`.

The authorized repair command is exposed through Shop Settings with a non-destructive confirmation. It uses the safe owner chain, clears the current cursor/sale-safe markers, and reruns the controlled repair path. Sales remain blocked only when the catalog is genuinely not sale-safe; a temporary ordinary delta failure does not invalidate an already safe offline snapshot.

## Persisted non-sensitive diagnostics

The shop-bound repository records sync mode, catalog version, cursor fingerprint, pages, received/accepted/skipped entity counts, expected counts, local active/distinct counts, duplicate/invalid/reference/orphan counts, price-ID evidence, pending prices, tombstones, non-authoritative active rows, duration, rows/sec, evaluation/verification timestamps, repair flag, redacted status code, checksum algorithm, and checksum fingerprints.

It does not persist payloads, credentials, PINs, tokens, cookies, full URLs with query data, or complete catalog responses.

## Local validation

The integrated branch has local PASS evidence for:

- Core catalog exactness and safety tests;
- malformed and backward-compatible contract cases;
- full-refresh mismatch, duplicate, orphan, `hasMore`, shop-binding, and repair cases;
- cross-page category/supplier relinking;
- remote price-ID exact retry and collision handling;
- `scripts/check-pos-catalog-pull.ps1`;
- `scripts/check-pos-sync-status-ux.ps1`;
- WPF `net48`/x86 build.

Final consolidated counts and release provenance are recorded in the closeout report and the post-commit evidence file under the external QA evidence root.

## Remaining Admin Web/runtime evidence

Admin Web must provide a stable complete summary on the final full-refresh page, define whether each count represents distinct active entities, and publish checksum canonicalization if checksum verification is desired.

After authorized credentials are available, the runtime proof must capture a redacted Admin Console view and a drained staging full refresh, then compare:

- expected products: 19,762 from the current task brief;
- authoritative categories, suppliers, and prices: currently unknown;
- local active and distinct remote counts;
- duplicate remote IDs and active barcodes: required zero;
- orphan/invalid reference mappings: required zero;
- pending prices and non-authoritative active rows: required zero;
- final `hasMore`: required false;
- shop binding and sale-safe evidence: required coherent.

Until those observations exist, staging catalog exactness and the 19,762 comparison remain `BLOCKED_EXTERNAL`, not PASS.
