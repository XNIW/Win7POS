# Win7POS catalog warning tolerance — 2026-07-27

Status: `REVIEW`.

This report will become the closeout record after the feature has passed local
validation, independent review, normal merge and real staging acceptance.

Current implementation accepts recovery only for catalog display text. It
canonicalizes ordinary whitespace, removes non-visual display controls, applies
NFC, replaces unpaired UTF-16 code units with U+FFFD, preserves valid
international text and emoji, and uses existing fallbacks without truncating.
Barcode, remote IDs, item number, pricing, counts and all structural catalog
invariants remain fail-closed.

The canonical values flow through validation, mapping, authoritative staging,
SQLite persistence and idempotency comparisons. A completed run with display
warnings remains sale-safe and records only aggregate diagnostics.

## Independent pre-PR review

The frozen feature was reviewed independently against `origin/main`. The
review raised one P1 (an invisible identity-format character reaching a display
fallback) and two P2 items (same-revision warning retention and transactional
warning state). All three were corrected and then re-reviewed. Final open
findings: P0=0, P1=0, P2=0, P3=0.

The follow-up uses identity-specific validation for barcode and remote IDs,
guards the mapper fallback for direct callers, and persists/reads the aggregate
warning state atomically under the active sync-generation fence.

## Post-merge verification

PR #45 merged normally at `de295018b1846581f015f3e0051b1a1894a452f7` after
the build, CodeQL and dependency/supply-chain checks passed. A clean-main
solution build and the local synthetic warning acceptance both pass.

Real allowlisted staging acceptance has not run: the required DPAPI profile
`C:\ProgramData\Win7POS\QaSecrets\asus-staging.dpapi` is absent. The
acceptance launcher exits with its explicit profile-missing code before it
creates or changes acceptance data. `DONE` remains prohibited until the profile
is securely initialized and the one real staging run passes.
