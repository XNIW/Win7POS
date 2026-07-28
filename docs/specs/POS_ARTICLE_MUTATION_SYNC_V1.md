# POS article mutation sync v1

## Scope

Win7POS consumes the Admin contract `pos-article-mutation-v1` through:

```text
POST /api/pos/catalog/article-mutations
```

The implementation is Windows 7 first, `.NET Framework 4.8`, x86. It changes
only Win7POS. Admin runtime/migrations, Supabase, Android, iOS and production
are outside scope.

Frozen Admin baselines:

- Admin main: `86713586106dc1e50bc5d846a24a257f521fc109`;
- Worker version: `56ec23b1-a5b7-4635-94ff-b2ebaa682d0f`;
- request fixture SHA-256:
  `deaf2948dd65bfc84da93957b571097cb967ab0023c923b6dc389ee74ebcc137`;
- response fixture SHA-256:
  `8b03c0a6110c752feaec86c45c8f4fc22dcc6e2d3dfcf629d894e444e01dc02f`;
- first-login fixture SHA-256:
  `9a2adbd0c4a4d928f5b986a094f7b154e0b274750b6fbe07a0df1dc4cea506df`.

Vendored byte-identical copies live under
`tests/fixtures/POS-ARTICLE-MUTATION-V1`.

## Manual write matrix

| UI / command | Repository transaction | Durable mutation | Focused evidence |
| --- | --- | --- | --- |
| New article Save | `ProductRepository.CreateLocalArticleAsync` → `LocalArticleMutationWriter.CreateAsync` | `product_create`, sequence 1 | local row/outbox atomicity; rollback; fixture/loopback |
| Duplicate Save | `CreateLocalArticleAsync` with `DuplicateSourceProductId` | `product_duplicate`, new local/client/mutation IDs | identity and remote assignment tests |
| Full Edit Save | `UpdateLocalArticleAsync` → `LocalArticleMutationWriter.UpdateAsync` | `product_update` for changed identity fields | exact field-mask and dependency tests |
| Barcode/item-number Save | same update transaction | `product_update` with exact changed keys | contract/repository/loopback |
| Primary/secondary name Save | same update transaction | `product_update` with exact changed keys | contract/repository/loopback |
| Category/supplier Save | same update transaction | verified remote reference, or durable `dependency_missing_remote_reference` block | missing-reference tests |
| Retail price Save | same update transaction plus one `product_price_history` row | `product_retail_price_change` | exactly-once history tests |
| Purchase price Save | same update transaction plus one `product_price_history` row | `product_purchase_price_change` | exactly-once history tests |
| Manual stock Confirm | same update transaction plus one `article_manual_stock_adjustments` row | `product_manual_stock_adjustment` with signed delta/reason | +5/-2 and sales-isolation tests |
| Deactivate command | `SetLocalArticleActiveAsync` | `product_deactivate` | ACK/reactivation loopback |
| Reactivate command | `SetLocalArticleActiveAsync` | `product_activate` | ACK/reactivation loopback |

Enqueue occurs only after an explicit Save/Confirm. Property setters and
keystrokes never enqueue. Local validation precedes the transaction. Entity,
price/stock side effect and outbox intent commit together; any exception rolls
the transaction back.

## Explicit write origin

Every public product write carries one `ProductWriteOrigin`:

- `LocalUserSave`;
- `SupplierImportApply`;
- `RemoteCatalogApply`;
- `ArticleMutationAck`;
- `SalesMovement`;
- `MaintenanceRestore`;
- `TestFixture`.

Only `LocalUserSave` creates article mutations. Supplier import continues to
use its existing import outbox. Catalog apply and ACK apply cannot echo.
Sale/refund/void stock continues exclusively through sales sync and generates
zero manual-article stock mutations.

## Canonical intent and wire boundary

`PosArticleMutationCanonicalWriter` writes compact UTF-8 JSON in this fixed
order:

1. `baseRevision`;
2. `changes`;
3. `clientProductId`;
4. `createdAt`;
5. `fieldMask`;
6. `idempotencyKey`;
7. `localSequence`;
8. `mutationId`;
9. `mutationKind`;
10. `occurredAt`;
11. `remoteProductId`.

`fieldMask` is ordinally sorted. Contractual nulls are retained.
`attemptToken` is excluded. The immutable hash is `sha256:` plus 64 lowercase
hexadecimal characters. A six-fraction UTC base revision is retained as text
and never rounded.

The transport constructs the trusted envelope only in memory from the current
protected session. No credential, PIN, device token or session token is stored
in the outbox. It enforces 1–25 requests and the actual encoded UTF-8 limit of
256 KiB, sends `application/json` with `no-store`, and never logs a request
body.

## SQLite durability

Migration `0010-article-mutation-outbox` is additive. It adds:

- `products.client_product_id`;
- `products.remote_base_revision`;
- `product_price_history.article_mutation_id`;
- `article_mutation_outbox`;
- `article_mutation_attempts`;
- `article_manual_stock_adjustments`;
- `article_product_remote_shadow`;
- state/sequence/product/attempt indexes.

Outbox states:

- `waiting_dependency`;
- `pending`;
- `in_progress`;
- `retry_wait`;
- `failed_blocked`;
- `completed`.

Create and later offline edits use one stable client product ID and increasing
local sequence. Before create ACK, later immutable intents remain
`waiting_dependency` without a fabricated remote ID or base revision. The ACK
transaction assigns the remote ID/revision, completes the current row and
seals only the next dependency. At most one mutation per product is
wire-eligible in a batch.

Claims and attempts are durable. An abandoned `in_progress` claim is recovered
without changing mutation/idempotency/hash. Transient failures retain the
payload and schedule bounded exponential backoff with deterministic jitter.

Backup naturally includes the SQLite tables. Restore, database replacement and
shop transition refuse unresolved waiting/pending/in-progress/retry/blocked
article work.

## ACK, replay and conflicts

The whole response is validated before any row is acknowledged:

- schema and overall code;
- exactly one unique result for every sent mutation;
- no missing/extra result;
- mutation/idempotency/hash identity;
- typed status and ACK shape;
- six-fraction revisions and UUID fields;
- current attempt for first apply;
- a previously persisted attempt for `duplicate_replay`.

An unknown attempt token or stale claim acknowledges nothing. ACK application
is one SQLite transaction that stores receipt metadata, remote price/stock IDs,
remote product identity and revisions, then releases the next dependency.

Terminal success:

- `applied`;
- `duplicate_replay`.

Retry/auth:

- `retryable_upstream` retains the same sealed mutation and schedules retry;
- `failed_auth` stops the trusted lanes and retains durable work.

Visible terminal blocks:

- `failed_validation`;
- `failed_conflict`;
- `target_not_found`;
- `identity_conflict`;
- `idempotency_payload_mismatch`;
- `dependency_missing_remote_reference`.

A correction is a new immutable mutation with a later sequence. There is no
blind last-write-wins.

## Pull protection and zero echo

Catalog pull updates the remote shadow/base. It does not delete a pending local
create and does not overwrite fields covered by waiting, pending, retry or
blocked local intent. A changed remote base blocks the stale local mutation
while retaining the local overlay and authoritative revision.

ACK/canonical reconciliation updates the base without creating article
outbox rows. Remote price IDs, article mutation IDs and manual stock mutation
IDs are unique, preventing duplicate local history or movement on replay/pull.
Authoritative full-refresh exactness and the unbounded 676-page drain remain
unchanged.

## Scheduling and operator status

`OnlineSyncLane.ArticleMutationOutbox` is a background lane below heartbeat,
auth and sales safety work. Claim selection is fair across products, bounded
to 25 items and 256 KiB, with one eligible mutation per product. A blocked
product cannot starve another product. The generation fence and single
supervisor prevent concurrent duplicate senders.

Sync Center exposes only aggregate pending/in-progress/retry/blocked/completed
counts, affected article count and a safe typed code. A conflict raises the
localized non-modal notice in English, Spanish, Italian and Simplified Chinese.
No raw payload or secret is displayed.

## Validation and staging

Local acceptance includes:

- golden contract/digest tests;
- atomic repository, dependency, fairness, replay, auth and restore tests;
- full Core/Data suite;
- WPF and UI harness Release net48/x86 builds;
- real WPF ViewModel loopback for the full mutation matrix;
- Sync Center/editor/conflict smoke at 1024×768 in four languages;
- authoritative 676-page exactness regression;
- supplier-import, sales-sync and restore regressions;
- DPAPI vault self-test and Gitleaks.

Real staging is test-only and exact-main gated:

- profile: `asus-staging`;
- isolated data:
  `C:\POSData\Win7POSArticleMutationAcceptance`;
- evidence:
  `C:\Dev\_codex-evidence\win7pos-pos-article-sync-v1-<RUN_ID>`;
- run ID: `ASUSART_<UTC_TIMESTAMP>_<RANDOM>`;
- one logical run per invocation, no automatic retry;
- fail-fast on retry-wait, auth denial, transport failure or HTTP 5xx;
- synthetic products only;
- zero sale/refund/void/print/scanner/drawer actions.

The cleanup manifest is the sole evidence artifact containing exact synthetic
IDs/barcodes. The generated Mac prompt scopes cleanup to those identities,
preserves immutable audit and prohibits an additional Worker deployment.
