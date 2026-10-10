# Dense 5000: exact C# wire capture for fresh Admin replay

First run: **1/1 PASS, 0 skipped**, 10.273 seconds, official Win7POS Core.Tests runner with the evidence-only external test import preserved under `runner/`.

This bundle contains 169 unmodified HTTP request bodies emitted by the production C# client and the 169 unmodified actual Admin pilot replies that drove them. The flow is Prepare, explicit retirement, commit, and ten individual sync/ACK operations on a read-only copy of the pinned source SQLite. The parent closes only after all ten ACKs. Exact 5,000 product IDs and 10,000 price IDs, unchanged history count, stock 1.25, retail 1200, purchase 900, immutable original bytes/hash, and cleared pending plan/queue were checked. See `capture/sqlite-readback.json` and the recovered SQLite.

Scope: happy-path capture from actual archived replies with synthetic outer trusted-session/auth fixtures. This is not yet proof that a fresh Admin database accepted these exact C# requests. Dense crash/retry/lost-response cases, the 59,999-row upper limit, deployed Worker/shared TEST, and physical Win7 are not claimed.

## Replay inputs

`capture/capture.manifest.json` binds each exact request/response byte file, route action, status, length, SHA256, and original pilot source index. Send each request body unchanged to the corresponding `/api/pos/catalog/import-recovery/<action>` through the official parser/handler and fresh equivalent isolated PostgreSQL fixtures. Do not replace the body with a parsed/reserialized object before the parser. Capture new actual response bytes/status/hash indexed against the exact request SHA256; preserve the original pilot replies separately. The C# follow-up will consume these new actual replies and verify mappings and state again.

`inputs/` preserves the builder/SQLite original, deterministic precommit plan, source SQLite, trust fixture, and original input manifest. The input manifest also lists source files outside this minimal transfer; only files explicitly listed in this bundle manifest are transferred. Source timestamps, identities and plan IDs are fixed in those inputs. There is no JSON rewriting or synthetic receipt/fence substitution in this capture.

## Selection and provenance

The untouched 210-response source ZIP and its manifest are under `provenance/`. `selection.manifest.json` explicitly partitions 169 selected source records and 41 additional pilot QA records, with every action, selector, status, byte length and response SHA. Selection follows actual server nextCursor/stage and full-ACK responses. The excluded cached plan readback is not delivery authority. All selected replies remain byte-identical to the source ZIP; request bodies are newly emitted by C#.

`source/` freezes the sole test-helper change, before/after SHA and patch. Its prior lowercase-vs-uppercase price-kind assertion issue was a static helper finding corrected before the first run, not a dense test failure and not the separate Admin upper-limit timeout. No product file changed. The transient test source and MSBuild import, first TRX, capture receipt, and exact binary hashes are retained. The test assembly for this evidence run includes that transient test; no permanent positive CI fixture has been introduced yet.

`bundle.manifest.json` inventories every transferred file except itself. The external transfer manifest additionally binds the ZIP SHA256. SQLite files are snapshots, never live databases. A verified pre-change backup remains on Asus; its path/size/hash are in the receipt rather than duplicating it in the ZIP.
