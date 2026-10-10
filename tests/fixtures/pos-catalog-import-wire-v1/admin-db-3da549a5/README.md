# Initial actual supersession responses: client incompatibility preserved

Published by Admin commit `3da549a5818e8db0cf863ff77425aedba392d043`.
ZIP SHA256: `d603a60e720f44590e35429fc5832e3e4e42da9be0b56acfacbd9c9198b9a3e0`.
Internal response-manifest SHA256:
`707d242f564a9f6e3db50626ef22503eabdfedebffed9909048480d01ec1742f`.

The archive records 115 unchanged C# request bodies from Win7POS `9a875502`
passing real Admin handlers and four isolated PostgreSQL databases. It pins
each response by scenario, index, route and request/response hashes. Outer
authentication and dependency fixtures remain synthetic. No staging or live
authentication is claimed.

The subsequent real C# reingest found an incompatibility: `plan.parts[]` lacks
`itemCount`, so the DTO reads zero and `QueryPlannedReceiptAsync` rejects the
receipt before the first child retirement. The initial runner result is kept
in `interop/reingested-supersession-db-3da549a5-first/interop-supersession-db-first.trx`.
These bytes are historical evidence, not a qualified C# roundtrip. They have
not been amended to add the field.

Four explicit historical negative tests subsequently passed: all four scenarios
reject this archive with `receipt_conflict` before sending their first child
retirement. TRX: `interop/reingested-supersession-db-3da549a5-negative/interop-supersession-historical-negative.trx`.
That expected rejection does not turn the initial roundtrip failure into a
positive interoperability result.

The first run also exposed a separate test-harness assumption: retrying an
immutable plan can return an advanced parent status after children were
accepted. Its identity, canonical hash and parts must stay fixed; parent
status need not. The test assertion was corrected separately. Retirement
retry bytes remain identical.

The Admin archive itself preserves its earlier harness failure that compared
the real root canonical hash to a synthetic generator hash. Its corrected
read-only postcheck did not resend already completed requests or apply effects.
