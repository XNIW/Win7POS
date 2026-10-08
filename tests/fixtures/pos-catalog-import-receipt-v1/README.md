# Coordinated Admin receipt and correction contract fixtures

Synthetic fixtures from the authorized Mac Admin lane on 2026-10-08:
`contracts/pos-catalog-import-receipt-v1`, worktree
`admin-pos-import-receipt-20261008`, baseline
`02ea44b95d4a05baddbf46f24251f0d5f0dea294`.
Source chat: `01a11d0a-e0f2-7025-88fa-ffaab827701f`.
The files were reconstructed from the exact fixture-generation commands and
verified byte-identical against their SHA-256 outputs. The test pins every
fixture hash. Final Admin source commit/runtime qualification remains separate.

Receipt lookup returns a flat identity envelope and the persisted RPC ACK, not
the old HTTP response. Accepted lookup includes current product snapshots with
six-fraction revision text. `not_found` is only a snapshot and never permits
replacement. Explicit retirement uses its own endpoint/schema and proves
`oldIdentityBlocked` with a durable timestamp.

Correction has current outer trust, `recoveryOf.originalRequest`, new operation
identities and mapped product/revision/fieldMask/changes. `baseSnapshot` contains
exactly the masked source fields, including an explicit null when the current
value is null; quantityDelta uses stockQuantity as its source. Only economic
fields are present in correction items; unchanged metadata is omitted.

The coordinated later seven-file set was acquired verbatim through root in
`u2/mac-golden-20261008`, and every copied SHA-256 matched the Mac receipt.
It includes normal and no-effect correction request/response pairs plus typed
child correction lookup, retirement and accepted lookup. Persisted ACK items
carry authoritativeRevision and unchangedFields. Omitting a requested price ID
requires the server's unchangedFields proof and equality with the bound base;
local snapshot equality alone is insufficient. Remote ACK barcodes can differ
from the original barcode without changing local history identity.

Successful lookup/retirement envelopes also bind originalSchemaVersion to the
exact ordinary or correction request. Four later verbatim files update ordinary
accepted/not-found/retired responses and add retire.accepted.response: retirement
must retain an operation already accepted remotely instead of claiming a new
fence. All four copied hashes matched the coordinator's Mac receipt; the test
pins every one of the thirteen fixture files.
All token strings are synthetic fixtures. These source fixtures and local
transport tests do not attest a deployed runtime or any live effect.
