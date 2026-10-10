# Exact multipart C# → Admin/PostgreSQL → C# responses

The response archive was published by Admin commit
`7faecb54406cd02e442e6548c039e0a2717196d2` without changing response bytes.
ZIP SHA256: `f813a28b28a6eabf10d651b4ac569d5a4fd1dc78e7a42086b3eb2226461afbbf`.
Manifest SHA256: `db9f39e376611a80d7767bf4da86ba64c6dca4b3d426731d75a2d05989ebbbba`.

The manifest binds each response to the route, exchange index, retry flag and
SHA256 of the unchanged C# request corpus published in Win7POS `7e287719`.
It includes all 39 requests plus the exact retry of exchange 036, actual
isolated PostgreSQL values and receipt maps, and the late-original RPC fence
returning `identity_retired` with no additional economic changes.

The C# test opens both archives with pinned hashes, verifies their manifests
and every request/response hash, and regenerates requests using the original
typed operation timestamp. It feeds the unchanged Admin responses into the
real HTTP/sync/recovery services and SQLite. The second case deliberately loses
the third apply response, waits the unchanged 30-second product backoff and
reopens factory/service before retrying the identical operation. The durable
receipt remains identical even though the recorded server parent metadata
has progressed from partial/3 to complete/5; local closure still requires all
five local ACKs. It compares every product and price ID plus economic values.

Scope: actual Admin parser/handler and economic SQL in a network-isolated
owned PostgreSQL instance, with explicit synthetic outer authentication,
session/lease and dependency schema. SQL/TypeScript hashes are recorded in
the manifest; the publication commit is not a claim that those changes were
merged or deployed. This under-4-MiB corpus does not qualify the aggregate
upper-size protocol. No shared TEST, Worker deployment or live authentication
is claimed.

The initial SQL correlation failure and the separately cancelled 882-second
plan query remain in `historical-failures/`. Subsequent successful responses
and read-only postchecks do not erase or relabel those earlier outcomes.
