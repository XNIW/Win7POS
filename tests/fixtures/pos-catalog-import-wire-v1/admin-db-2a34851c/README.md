# Qualified C# supersession roundtrip with authoritative part counts

Published by Admin `2a34851cde013fc11cd90875479aada8350f0bec`, executed from
`54a7a3cf7c01e60ea032d9d7444e9b445fa36511` with multipart SQL SHA256
`c827afcaeae4fc4270b0b0d5078c4d7ce7084ff9d315bde4492f46982d3a80cf`.

Response ZIP SHA256:
`7baef9303b7be9c20ae4dc183aae0e305c4948efea035a7ab42dc3521d876797`.
The adjacent `manifest.json` is the unchanged internal response manifest:
SHA256 `0474f4e6b907dd13c9983a6ca5be7fb4ca3978f79ecd7c6c89f40fd6507bc1ca`.

The official C# runner passed eight tests: four positive roundtrips using all
115 original request bodies and four expected refusals of the older archive
in `admin-db-3da549a5`. TRX:
`interop/reingested-supersession-db-2a34851c-final/interop-supersession-db-c827-final.trx`.

The positive cases regenerate the original typed operation timestamps and
verify unchanged builder/SQLite originals, then bind every HTTP request and
response to scenario, index, route, length and SHA256. The actual response
bytes pass through the real HTTP/recovery/sync services. A deliberately dropped
reply still represents completed server execution and is dropped locally before
the unchanged operation is retried. There is no synthetic response fallback.

Each case compares every one of 1001 product IDs and 2002 price IDs from actual
database receipts with SQLite mappings, plus stock, purchase/retail values and
unique local history. Accepted predecessor work is carried without another
economic apply. A partial successor remains unresolved at 1000/1001 rows and
1/2 parts, then closes only at 1001/1001 and 2/2. Zero-child successors close
after their authoritative registration without any successor apply. Deferred
operator edits remain attached to the accepted predecessor and survive reopening.

The initial corrected-bundle run reached local closure in all four cases but
its final test assertion compared lowercase wire price labels with uppercase
SQLite labels. That separate harness failure remains in
`interop/reingested-supersession-db-2a34851c-first/interop-supersession-db-c827-first.trx`.
Only the known `purchase`/`retail` enum labels are normalized for comparison;
barcode and remote ID comparisons remain exact. No archived JSON was changed.

Scope: actual Admin handlers/parser and economic SQL in four independently
owned isolated PostgreSQL databases, with explicitly synthetic outer
authentication and dependency schema. This proves the recorded interoperability
path; it does not qualify live authentication, shared TEST, staging deployment,
the aggregate upper-size protocol or a physical POS installation. Historical
failures and expected rejections remain separate from the positive result.
