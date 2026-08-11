# Win7POS staging catalog product row invalid handoff

## Ownership decision

This is a staging server/data-contract defect. Win7POS received a product DTO whose
`productName` violates the established safe-text contract. The POS correctly remains
fail-closed. Do not weaken `ProductStageFingerprint`, skip the row, synthesize a name,
or force the catalog sale-safe.

## Revisions and observation

- Win7POS merged main SHA: `c59f91ddefeb4bee7848671deb5a99137d59ec49`
- Win7POS diagnostic source base SHA: `065ae9752b1a5cb6f4c3b700c7b37e6fb3b0a8dd`
- Admin Web main SHA: `54889a68a65cec39764bbb5479574e942f4d54f1`
- Observation UTC: `2026-07-27T04:11:19.4518875Z`
- Environment: staging only
- Catalog revision HMAC-SHA256: `222628ae3691a14c268ea1b2f674fe812b6ccc2f7b25ee74a8b4e8b001dc929b`
- Failure correlation HMAC-SHA256: `31bb744ff96e36d8db7a63b535d6533dd7884208fefb44736baace5eb28d9ac3`
- Invalid count in the rejected response: `1` first observed invalid product
- Page/lane/row: page `3`, lane `products`, row `56`
- Client error: `catalog_product_row_invalid`
- Exact redacted reason: `invalid_product_name_text`

The HMAC key remains outside the repository in the local evidence directory. No
credential, identifier, barcode, product name, request body, or raw payload is included
in this handoff.

## Redacted field evidence

| Field | Observation |
|---|---:|
| `productId` UTF-16 length | 36 |
| `barcode` UTF-16 length | 13 |
| `productName` UTF-16 length | 38 |
| `secondProductName` UTF-16 length | 19 |
| `itemNumber` UTF-16 length | 6 |
| `categoryId` UTF-16 length | 36 |
| `supplierId` UTF-16 length | 36 |
| `updatedAt` UTF-16 length | 32 |
| retail price class | positive, converts to `long` |
| purchase price class | non-negative and in range |
| stock quantity class | non-negative and in range |

`productName` is shorter than the 512-character maximum. Therefore
`RemoteCatalogContentPolicy.IsOptionalText` can only reject it because the decoded
string contains a control character or an unpaired UTF-16 surrogate. The value itself
must not be copied into logs or issues.

## Raw JSON to DTO to mapped row

1. Raw JSON: the rejected response body was intentionally not persisted. Use the
   staging request logs and authoritative dataset around the observation timestamp to
   identify page 3/product row 56. Do not export the full response into GitHub.
2. DTO: transport deserialization completed. The resulting
   `PosCatalogProductResponse.ProductName` had length 38 and failed the exact safe-text
   rule as `invalid_product_name_text`.
3. Mapped row: not produced. Compatibility validation runs before
   `RemoteCatalogBatchMapper`, so the complete page was not staged and no live catalog
   row was mutated.

## Required server correction

1. Reproduce the full paginated staging response, including pages 1, 2, and 3; do not
   validate only the first page.
2. Locate the authoritative record using the timestamp, catalog revision, page/row,
   and HMAC correlation above without publishing its identifiers.
3. Correct the authoritative write/import boundary so product names containing control
   characters or malformed Unicode cannot be stored or emitted.
4. Repair the affected staging record at its authoritative source. Do not mutate
   Supabase from the ASUS POS and do not touch production.
5. Add a response-contract test that walks every product in the real-volume paginated
   fixture and applies the same safe-text rules as Win7POS.
6. Add focused fixtures containing an embedded control character and an unpaired
   surrogate; the server must reject or explicitly normalize them at the write/import
   boundary before catalog export.
7. Keep the existing timeout and non-empty catalog behavior. Deploy only to staging.

## Reproductive client fixture

The Win7POS diagnostic regression uses a synthetic name containing an embedded newline.
It must produce `invalid_product_name_text`, preserve the value from all logs, and leave
the product unmapped. This fixture is synthetic and contains no staging data.

## Completion contract

After the server PR passes CI, merge normally, deploy staging, and verify the full
catalog response at real volume. End the Mac task with exactly
`READY_FOR_ASUS_AUTOMATED_ACCEPTANCE`. Only then should the ASUS run one automated
staging acceptance.
