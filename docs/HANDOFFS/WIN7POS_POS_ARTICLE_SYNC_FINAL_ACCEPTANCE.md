# Win7POS POS article sync final acceptance

Status: `DONE`

Phase: `DONE`

Resolution: `USER_CONFIRMED_CLOSURE`

Cross-repository cleanup: `READY_FOR_MAC_FINAL_CLEANUP`

Windows 7 physical: `EXTERNAL_PENDING`

## Frozen baselines

- Initial Win7POS main: `82efe5b687b4be78a3a4dbde88be0bf384604b44`
  (PR #68 merge).
- Final acceptance software main:
  `2eeb58b11f0958e4c03f35a0181a0e7adfbec400`.
- The focused closeout PR containing this document changes documentation only;
  its merge commit is the final repository main and does not change the
  accepted binaries.
- Admin main, read-only:
  `e1783f57509c8011902c1f076d3b1f5ee2e56309`.
- Admin runtime source, read-only:
  `9fb54f50999b8587bc37f5e2040743df20df8f08`.
- Worker deployment/version prefixes: `5ad3652d` / `57af0535`.
- Final run:
  `ASUSART_POST_PR68_20260730T011055932Z_9ED62AFB`.

Admin runtime, Admin migrations, Supabase schema, Android, iOS, production,
billing and hardware were not modified. No cleanup was executed on the Asus
machine.

## Local acceptance

- Core/Data: 746/746, skipped 0.
- Cleanup-manifest finalization: valid 4 distinct price IDs / 3 distinct manual
  movement IDs accepted; obsolete 2/2, missing, duplicate, unrelated,
  sale-origin and count-mismatch cases rejected.
- WPF and UI harness: Release `net48`/x86, 0 warnings, 0 errors.
- Dialog standards: 34/34.
- Required gates: 45/45.
- Final article-sync static gate: PASS.
- Solution Release: 0 warnings, 0 errors.
- Full article loopback: PASS, including
  `cleanupManifestRemoteChildCounts=True`.
- Authoritative drain: 676 pages, exactness `Verified`, terminal
  `hasMore=false`, `repairRequired=false`, sale-safe true.
- Atomic/streaming and focused failure regressions: 114/114.
- Runner parser/single-instance/timeout, restore guard, Supplier Excel,
  bootstrap contract/diagnostics and STA UI capture: PASS.
- Gitleaks 8.30.1: working tree, relevant history and final evidence all have
  0 findings.

## Public-staging acceptance

The successful run used one isolated data directory and one logical run. It
completed HTTP 200 first login, trusted-session persistence, process restart,
bounded online recovery and POS unlock. Catalog exactness matched:

- products: 19,779;
- categories: 71;
- suppliers: 102;
- prices: 41,252;
- pages: 676;
- skipped rows: 0;
- exactness: `Verified`;
- terminal `hasMore=false`;
- repair required: false;
- sale-safe: true.

The mutation matrix passed offline create and dependent edit, restart
persistence, create/update, primary and secondary name, barcode/item number,
category/supplier, retail and purchase price, stock +5/-2, duplicate,
deactivate, authoritative tombstone convergence, reactivate, replay,
same-ID/different-payload rejection, stale conflict, unrelated fairness,
explicit conflict correction/supersession, canonical pull and zero echo.

Final state:

- waiting dependency / pending / in progress / retry wait / blocked: 0;
- sales and revenue rows: 0;
- hardware actions: 0;
- final-run cleanup manifest: 3 products, 4 price-history IDs, 3 manual
  stock-movement IDs, 16 mutation receipts, 1 conflict receipt and 19 exact
  disposable sync events;
- evidence completeness, log redaction, evidence redaction, boundary-aware
  numeric scan, screenshot privacy and orphan-process checks: PASS;
- independent runtime-correction review:
  P0/P1/P2/P3 = 0/0/0/0.

## Remediation and evidence

- PR #69 isolated acceptance in the post-PR68 namespace.
- The first new logical run completed the business matrix but failed closed
  because successful zero-finding Gitleaks reports were rewritten as empty
  files.
- PR #70 preserved zero-finding reports as the non-empty JSON array `[]` and
  added a supply-chain regression guard.
- The distinct exact-main run after PR #70 is the successful final run above.

External evidence leaf:
`win7pos-final-post-pr68-ASUSART_POST_PR68_20260730T011055932Z_9ED62AFB`.
It is intentionally not committed because it contains exact synthetic cleanup
identities. Its consolidated manifest SHA-256 is
`ECA9F9158BF5B026FF6CD59C875CEE1FBB158E6608EE0E915C5AA70ABFDEE892`.

The external evidence contains:

- `CONSOLIDATED-CLEANUP-MANIFEST.json`, covering all six historical groups and
  the successful post-PR68 group;
- `NEXT-CODEX-MAC-FINAL-CLEANUP.md`, ready to execute beside that manifest;
- fresh SELECT-only validation with exact ownership, counts and relations;
- `cleanupExecutedOnAsus=NO`.

Consolidated scope is 7 groups, 18 products, 28 price-history rows, 21 manual
stock movements, 94 mutation receipts, 4 conflict receipts and 118 disposable
sync events. Every exact ID is present and QA-owned; sale-origin movements,
unexpected related rows and already-absent IDs are all zero. Cleanup remains
`NOT_EXECUTED`.

## Closure

- Article-sync client task: `DONE`.
- Authoritative catalog drain task: `DONE`.
- `ASUS-W7POS-013`: `DONE`.
- Final staging acceptance: `PASS`.
- P0/P1/P2/P3: `0/0/0/0`.
- Cross-repository cleanup: `READY_FOR_MAC_FINAL_CLEANUP`.
- Windows 7 SP1 physical validation: `EXTERNAL_PENDING`; this does not reopen
  the accepted software scope.
