# Win7POS catalog warning tolerance — 2026-07-27

Status: `EXECUTION`.

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
