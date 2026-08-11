# ASUS-W7POS-015 — Trusted POS product image Phase B

## State

- Phase: `EXECUTION`
- Status: `EXECUTION`
- Owner: `CODEX / ASUS`
- Activated: `2026-07-31`
- Admin coordination: `TASK-150`
- Branch: `codex/asus-product-image-phase-b-final-20260731`
- Phase A merge baseline:
  `9bc5b757b78fe7b9212bf5fae359a5559e3da7f9`
- Physical Windows 7: `NOT_RUN`

`ASUS-W7POS-014` is already used by the historical catalog SQLite batch task,
so `ASUS-W7POS-015` is the next free repository-wide Asus task ID.

## Frozen cross-repository pins

- Admin main/runtime inspected:
  `d2689f15f0291670bbc2713967368521e3f3a7fe`
- Admin TASK-149 runtime ancestor:
  `1de2912419f6770ff1ef7c6819754f4439ab849f`
- Admin TASK-149 tooling ancestor:
  `d3c674ada8aa7abf0179355c09238472b9ff3023`
- Android read-only:
  `4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e`
- iOS read-only:
  `c1b7b706c5f05cd7e8dda74cea1122f6483df7ec`
- Portable contract SHA-256:
  `b6212f36f27a6dc294713ca7345a29ff8d1a73733b9edb5d8e1a5c3b8ec14672`
- POS schema SHA-256:
  `74bd4b7f86a05b6180c133c86a47ae70be99a6f8012c8bfb747d7b18c714ceb0`
- Fixture manifest SHA-256:
  `ebb67b47d1460fa6361aa0d06e490f39e3b4a74afcd0cafc9fa9decc19e1df05`
- TASK-149 migration SHA-256:
  `b4eb344f4bb73ae8cfbcb5ef10ed53f2959694caf814c53c78978d7c450d6511`
- Admin handoff SHA-256:
  `605d400b0074166991c185b0120aea78bc3a2924c447e7112796f680c88d7d87`

## Scope

Implement the exact `pos-product-image-v1` client on net48/x86 with typed
canonical contracts, memory-only signed URL leases, SQLite migration `0011`,
a dedicated durable image-operation outbox, catalog image projection, Phase A
cache/decode activation, choose/replace/remove UI, localization, accessibility,
restore protection, local and staging acceptance, and mandatory exact cleanup.

The Admin work is limited to the separately reviewed staging-only TASK-150 QA
provisioning/cleanup boundary. Android, iOS, production, billing, real product
data, peripherals, sales semantics and article protocol semantics are outside
scope.

## Required delivery gates

- contract/schema/fixtures byte-identical and golden digests;
- full Core/Data, focused image, WPF imaging, WPF/UI smoke and Release x86;
- dialog, architecture, product-image, article/catalog regression gates;
- Supply Chain, CodeQL, Gitleaks and package completeness;
- independent review with `P0/P1/P2/P3 = 0/0/0/0`;
- non-draft PR and normal merge;
- real staging matrix and terminal zero-residual cleanup;
- physical Windows 7 PASS when reachable, otherwise exact
  `EXTERNAL_PENDING` installer/checklist handoff.

Evidence is recorded in
`docs/reports/ASUS-W7POS-015_PRODUCT_IMAGE_PHASE_B_EVIDENCE.md`.
