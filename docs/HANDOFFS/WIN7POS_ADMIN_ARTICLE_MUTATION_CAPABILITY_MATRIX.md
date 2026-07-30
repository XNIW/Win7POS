# Win7POS -> Admin article mutation capability matrix

Status: `DONE`

Resolution: `USER_CONFIRMED_CLOSURE`

Audited: 2026-07-28 UTC, read-only

## Scope and evidence boundary

This is a contract-capability audit, not an implementation or an Admin change.
It was read from `merchandise-control-admin-web` at
`origin/main` = `7ff0f6a0dfd9e1203cd07834f73ecc4269abc714`.

The only POS catalog write route present at that revision is
`POST /api/pos/catalog/import-sync`. Its schema version is
`pos-catalog-import-v1`, requires `source: "supplier_excel"`, and submits an
import batch. The other POS catalog route is pull/read only. No POS article
mutation route was found.

| Surface | Authentication and version | Capability boundary | Evidence |
| --- | --- | --- | --- |
| `POST /api/pos/catalog/import-sync` | POS device token + POS session; `pos-catalog-import-v1` | Supplier-Excel batch import only; `source` must equal `supplier_excel`; accepted rows are `new` or `updated`. | Admin `src/app/api/pos/catalog/import-sync/route.ts`, `src/server/pos-auth/catalog-import-sync.ts` (`parseCatalogImportInput`, `parseCatalogImportItem`), `src/server/pos-auth/pos-contract.ts` |
| `POST /api/pos/catalog/pull` | POS session; `catalog-v2` | Catalog delivery/read boundary, including an expected revision for the pull snapshot; it is not a mutation precondition. | Admin `src/app/api/pos/catalog/pull/route.ts`, `src/server/pos-auth/catalog-pull.ts`, `src/server/pos-auth/catalog-revision.ts` |
| `staff_web_catalog_mutate_v1` RPC | Staff **web** lease-bound session | Internal Admin-Web mutation boundary with operations such as `product_create` and `product_update`; not an HTTP POS-device contract and has no POS schema/version or POS ACK model. | Admin `src/server/shop-admin/staff-web-lease-bound-rpc.ts`, `src/server/shop-admin/staff-aware-mutations.ts` |

The TASK-094 import RPC (`pos_catalog_import_apply_v2`) stores a batch
idempotency key and payload hash, persists replay ACKs, and can return product
and price IDs. That makes it suitable only for its declared import protocol; it
does not turn the importer into a safe manual-article mutation API.

## Required Phase B capability matrix

`Partial` means a field happens to exist in the supplier-import row, but the
current contract is still not safe or sufficient for manual POS article edits.
It must not be consumed as an undocumented compatibility API.

| Required operation | Current POS contract support | Verdict | Exact gap / evidence |
| --- | --- | --- | --- |
| Create an article | `changeKind: "new"` may create an import row and returns a product ID. | Partial — not usable | It is only a non-empty `supplier_excel` batch, not a manual create operation; no per-mutation intent or field mask. Import parser/RPC above. |
| Duplicate an article | No duplicate/cloning operation. `duplicate` only means replay of the same import batch. | No | `PosCatalogImportEndpointResult.batch.status` uses `duplicate`/`idempotent` for batch replay, not product duplication. |
| Edit an existing article | `changeKind: "updated"` accepts a complete import row. | Partial — not usable | No target remote ID requirement, no field mask, and no per-field intent. The SQL updater resolves a live product by barcode and coalesces nullable values. Admin `supabase/migrations/20260706120000_task_094_pos_catalog_import_apply_rpc.sql`. |
| Name, barcode and item number | Import row carries `productName`, `barcode`, and `itemNumber`. | Partial — not usable | Barcode is import identity; an independent barcode edit/rename semantic, target ID, and conflict policy are absent. |
| Category and supplier assignment | Import row accepts category/supplier **names**, and the RPC may resolve/create them. | Partial — not usable | No target category/supplier IDs, assignment ACKs, or controlled manual-edit semantics. The response returns only product and price IDs. |
| Retail and purchase price | Import row accepts `retailPrice` and `purchasePrice`; the ACK may return price IDs. | Partial — not usable | Values are part of a full supplier-import row. There is no dedicated price mutation with target identity, field intent, concurrency rule, or price-effective policy. |
| Manual stock adjustment | Import row accepts an absolute `quantity`/`stockQuantity`. | No | No delta/adjustment operation, reason, stock ledger reference, or replay-safe adjustment identifier; using an absolute import quantity would be unsafe for manual adjustments. |
| Activate / deactivate article | No POS activation/deactivation operation. | No | Import SQL may revive a row by clearing `deleted_at`; this is not an explicit lifecycle API and cannot be used as one. |
| Optimistic concurrency | None for a POS mutation. | No | `expectedRevision` exists for catalog **pull** snapshots, not as a mutation precondition. Import request/RPC has no base revision or row version. |
| Immutable idempotency per mutation | Batch `idempotencyKey` plus `payloadHash`, with replay ACK. | Partial — not usable | Scope is an import batch. There is no immutable per-article mutation ID, attempt token, mutation-hash verification, or outcome keyed to individual manual mutation. |
| ACK matching, remote IDs and retry outcome | Batch ACK echoes batch IDs/hash and returns product/price IDs. | Partial — not usable | No stable individual mutation ID or attempt token; no remote IDs/ACKs for category/supplier/lifecycle/stock-adjustment operations. |

## Blocking decision

Phase B code must not be added yet. Sending manual article edits through
`pos-catalog-import-v1` would silently redefine a supplier Excel import
protocol, weaken concurrency/stock safeguards, and make retries ambiguous.
No staging catalog writes are authorized while this matrix is blocked.

The separate Phase B prerequisite is an Admin-owned, versioned POS article
mutation contract. At minimum it must provide:

1. A dedicated authenticated POS endpoint and explicit contract version, with
   typed operations for create, duplicate, field-level update, lifecycle, and
   stock adjustment.
2. Target remote IDs and a `baseRevision` (or equivalent row version) for every
   non-create change, with a deterministic conflict result.
3. Explicit field intent/field mask so omitted values cannot overwrite live
   data, plus separate semantics for stock delta and absolute import quantity.
4. Immutable per-mutation ID, attempt token and canonical payload hash; replay
   must return the original outcome and reject same-ID/different-payload input.
5. A fully correlated ACK containing mutation ID, status/code, target remote
   IDs (including newly assigned category/supplier/price IDs where applicable),
   authoritative revision, and a retry-safe terminal outcome.
6. Transactional publication of the resulting catalog revision/sync event so a
   follow-up pull can reconcile exactly what the ACK accepted.

After that Admin contract is merged and its staging deployment is separately
verified by its owner, Win7POS can implement the Phase B outbox and add only
synthetic, authorization-approved staging coverage.

## Superseding final closure

The blocking audit above is retained as historical evidence. The required
Admin contract was subsequently delivered and the Win7POS client was
implemented and accepted from exact main. The final public-staging proof is
recorded in
`docs/HANDOFFS/WIN7POS_POS_ARTICLE_SYNC_FINAL_ACCEPTANCE.md`.

Article-sync client task and Phase B are `DONE`; final staging acceptance is
`PASS`; P0/P1/P2/P3 is `0/0/0/0`. The remaining cross-repository action is
exact-ID synthetic cleanup with status `READY_FOR_MAC_FINAL_CLEANUP`.
