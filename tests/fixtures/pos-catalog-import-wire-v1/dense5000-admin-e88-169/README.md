# Dense 5000: actual Admin replies reingested by C#

This fixture closes the happy-path byte-exact roundtrip for 5,000 dense rows:

1. The real C# builder/SQLite/services emitted the 169 request bodies pinned in the adjacent `dense5000-csharp-169` fixture (Win7POS `cd89ba8472c1f02d643e1ac027af183cb995d915`).
2. Admin source `2bcee6e84c5a6a8cdb5c8165bf235848b981152d`, SQL SHA256 `e88d17783f89fc307bb2f62c1d2e5d6fb645fa6d3c3b3a4b7d723b50d94f9aca`, consumed those bodies unchanged through the actual parser/handler and historical apply/U2 functions on the new isolated database `pos_interop_current_dense5000_csharp169e88`.
3. The first official C# reingest passed 1/1, with no skipped tests, using all 169 actual responses and checking every regenerated request SHA256. It completed Prepare, explicit retirement, commit and ten single-part sync/ACK operations on a copy of the pinned SQLite.

The test verifies 5,000 actual product IDs and 10,000 actual price IDs, stock 1.25, retail 1200, purchase 900, 10,000 unchanged local history rows, unchanged original bytes/hash, all ten ACKs, parent closure only after the last ACK, and no unresolved queue or prepared plan. The permanent test is `Dense5000_ActualFreshAdminReplies_ReenterExactCSharpRequestsAndCompleteRecovery`; missing or changed fixture bytes fail the test. It has no synthetic response fallback and does not skip when evidence is absent.

## Immutable files

- `exact-csharp-dense5000-169-e88-minimal-csharp-reingest.zip`: 1,069,478 bytes; SHA256 `3351fd42d40c5b07385f3aa61cc00dc84f513a615494ee7dd9e4da6c617880c5`.
- Inside the ZIP, `schedule.json`: SHA256 `de0548d0435b6ce886e00a9e1a1fa2317409eac0a10c129fb1f0a23bd9d9c617`. It orders 169 actions with their actual HTTP statuses and exact request/response hashes.
- Inside the ZIP and copied beside it, `manifest.json`: SHA256 `3dec396f32ea18666f56e23f3628e0b29d49fbee5b93bd630ed46eb65422d6e6`. It preserves server provenance, economic postcheck, original archive hashes and all response hashes.
- `transfer-receipt.json` records verification of the read-only segmented delivery, including the 171 ZIP members. The original Admin full archive and pilot archive remain separately preserved. Response bytes were not reserialized.

The response-only ZIP reuses request bytes and input SQLite from the adjacent capture fixture; it does not duplicate them. The server manifest preserves an earlier evidence-harness assertion failure that confused a complete child receipt with a completed parent. That historical failure was corrected without replaying a completed apply. It is distinct from the first successful C# reingest and from the upper-volume timeout.

Scope: actual parser/handler and isolated PostgreSQL functions, with synthetic dependency schema and outer lease/token fixtures. This does not qualify deployed/live authentication, shared TEST, Worker rollout, physical Win7, the 59,999-row upper limit, or dense crash/retry/lost-response scenarios. The first C# result and SQLite readback are retained in the Asus evidence bundle as `large-proof/dense5000-e88-reingest-first.trx` and `large-proof/dense5000-e88-reingest-first/`.
