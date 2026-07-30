# WIN7POS-CATALOG-TOLERANCE-001

## Task

- Tracking key: `WIN7POS-CATALOG-TOLERANCE-001`
- Local task ID: `ASUS-W7POS-013` (next free Win7POS ASUS task ID)
- Status: `DONE`
- Phase: `DONE`
- Resolution: `USER_CONFIRMED_CLOSURE`
- Base: `e1dcbe7757d06b9e2ec30102d8654ea5e12412c6`

## Scope

Recover only catalog display fields (`ProductName`, `SecondProductName`, category
name and supplier name) on a copied transport payload. Identity, barcode, item
number, prices, relationships, pagination, manifests and sale-safety fencing
remain strict blockers.

The recovery is deterministic and idempotent: NFC, permitted whitespace to one
ASCII space, bounded control/format removal, valid emoji preservation (including
ZWJ), and U+FFFD for a materialized unpaired UTF-16 surrogate. It never silently
truncates; an over-limit display value uses an existing product or remote-ID
fallback and records only an aggregate warning.

## Evidence structure

- Shared cross-platform fixture: `tests/fixtures/CATALOG-TEXT-001/catalog-text-policy-v1.json`
- Win7POS consumer-tolerance fixture:
  `tests/fixtures/CATALOG-TEXT-001/win7pos-catalog-consumer-tolerance-v1.json`
- Evidence index:
  `docs/reports/evidence/WIN7POS-CATALOG-TOLERANCE-001/README.md`
- Final report:
  `docs/reports/2026-07-27_WIN7POS_CATALOG_WARNING_TOLERANCE.md`

## State transitions

`PLANNED` → `EXECUTION` → `REVIEW` → `DONE`.

`DONE` is allowed only after focused tests, independent review with no open
P0/P1/P2 findings, normal PR merge, required CI and one real DPAPI staging
acceptance. Windows 7 physical validation remains `EXTERNAL_PENDING` unless it
is run on Windows 7 SP1 hardware.

## Post-merge review state

- PR #45 merged normally at `de295018b1846581f015f3e0051b1a1894a452f7`.
- Required CI, CodeQL and supply-chain checks passed; final independent review
  has P0=0, P1=0, P2=0, P3=0.
- The post-merge local synthetic warning acceptance passes. The DPAPI profile
  was initialized through the approved protected path and one real allowlisted
  staging acceptance was executed. Bootstrap failed before catalog pull with
  the redacted code `bootstrap_failure`; no second run is permitted until the
  staging-side cause or credential-field mapping is clarified.

## Final closure

The earlier failure was superseded by the final exact-main acceptance recorded
in `docs/HANDOFFS/WIN7POS_POS_ARTICLE_SYNC_FINAL_ACCEPTANCE.md`. The public
staging run completed the 676-page authoritative catalog, matched all product,
category, supplier and price counts and identities, retained sale safety, and
completed the article mutation matrix with zero pending or blocked work.

Final staging acceptance is `PASS`; P0/P1/P2/P3 is `0/0/0/0`.
Cross-repository cleanup is `READY_FOR_MAC_FINAL_CLEANUP`. Windows 7 SP1
physical validation remains `EXTERNAL_PENDING`.
