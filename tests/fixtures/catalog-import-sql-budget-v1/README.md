# SQL item budget regression input

`dense523.child-projection.json.gz` preserves the exact first child projection
from the isolated real C# `9c19bbe` / Admin `15ee1886` calibration 5000 run
`fbf7d6681eaf49f59e8a1d6e0ceafc53`. Its projection is 520146 UTF-8 bytes. Removing
only the transient payloadHash and attemptCount reproduces the persisted payload
SHA-256 recorded in provenance.json. There are no authentication fields.

The actual HTTP request fit 524288 bytes, but PostgreSQL observed items binary
size 572424 and JSONB text size 554058. Real apply returned `validation_failed` and
products/prices/batches stayed 0. This is a preserved failure input, not a new
server response, acceptance or maximum qualification. The focused regression
requires new planning to partition it under both unchanged budgets while leaving
its original persisted bytes, IDs and hash unchanged.

The additional tests preserve all 1000 nullable/fallback/NFC-expanding/supplementary
rows and exercise numeric extrema. ASCII compact live succession still partitions
as 1000+1. A short non-ASCII name reserves the full accepted field limit: this
intentional conservative tradeoff avoids depending on OS/V8 Unicode tables and
can increase part count. Historical archived schedules keep their own original
plans; newly generated plans require new responses and qualification.

The explicit tradeoff regression measures a one-character name `漢` at the same
720-byte display reserve as 240 such characters, 719 bytes above ASCII `A`.
Identity text remains exact. This reserve covers NFC expansion and different
OS/V8 Unicode tables without attempting to reproduce PostgreSQL binary encoding.
