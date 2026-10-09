# Current UTC ordinary 5000-row Admin database responses

The unchanged archive was fetched from Admin commit
`bee0670a6b9b9fd56d2abb9f29e22ccc45293888`, path
`contracts/pos-catalog-import-recovery-multipart-v1/interop/current-utc-5000-handler-db-responses.zip`.
SHA256: `424c58dc40ae01bedc352f84464b1e6962b01e9d88da90aa55c166e73672c5b4`.

It binds the exact `candidate-current-utc/5000-planned-import` request bytes
from Win7POS bcba4fef to five real Admin handler/isolated PostgreSQL apply
responses and 15 passing database postchecks. Outer lease/token authorization
uses declared fixture dependencies, not deployed authentication.

The real C# HTTP/sync service reingested all five replies unchanged and verified
every one of 5000 product IDs and 10000 price IDs, final values, immutable
originals, and group completion only after the last ACK. This qualifies the
recorded current-UTC ordinary corpus, not the pending multipart implementation
or a deployed runtime.
