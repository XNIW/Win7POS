# ASUS-W7POS-015 — Product image Phase B evidence

## Current state

- Phase: `EXECUTION`
- Status: `EXECUTION`
- Admin coordination: `TASK-150 ACTIVE / EXECUTION`
- Phase A PR `#72`: `MERGED_NORMAL`
- Phase A head:
  `7042d88bb4d2d30e38ef48e5f5ff83ced39db9a2`
- Phase A merge:
  `9bc5b757b78fe7b9212bf5fae359a5559e3da7f9`
- Phase A CI, CodeQL and Supply Chain: `PASS`
- Phase A independent review: `P0/P1/P2 = 0/0/0`
- Physical Windows 7: `NOT_RUN`
- Production/Android/iOS: `NOT_MODIFIED`

No Phase B or staging gate is promoted to PASS before it is actually run.

## Frozen pins

| Artifact | Revision / SHA-256 |
| --- | --- |
| Win7POS Phase B baseline | `9bc5b757b78fe7b9212bf5fae359a5559e3da7f9` |
| Admin inspected main | `d2689f15f0291670bbc2713967368521e3f3a7fe` |
| Android inspected | `4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e` |
| iOS inspected | `c1b7b706c5f05cd7e8dda74cea1122f6483df7ec` |
| Portable contract | `b6212f36f27a6dc294713ca7345a29ff8d1a73733b9edb5d8e1a5c3b8ec14672` |
| POS schema | `74bd4b7f86a05b6180c133c86a47ae70be99a6f8012c8bfb747d7b18c714ceb0` |
| Fixture manifest | `ebb67b47d1460fa6361aa0d06e490f39e3b4a74afcd0cafc9fa9decc19e1df05` |
| TASK-149 migration | `b4eb344f4bb73ae8cfbcb5ef10ed53f2959694caf814c53c78978d7c450d6511` |
| Admin handoff | `605d400b0074166991c185b0120aea78bc3a2924c447e7112796f680c88d7d87` |

## Gate ledger

| Gate | Result |
| --- | --- |
| Admin TASK-150 QA boundary | `IN_PROGRESS` |
| Vendored contract/golden vectors | `PASS` |
| SQLite migration/outbox | `PASS` |
| Catalog/cache/client/UI | `PASS` |
| Focused/full local matrix | `PASS` |
| Independent Phase B review | `PASS — P0/P1/P2/P3 = 0/0/0/0` |
| Phase B PR/CI/merge | `NOT_RUN` |
| Real staging acceptance | `NOT_RUN` |
| Mandatory cleanup | `NOT_RUN` |
| Installer/package | `NOT_RUN` |
| Physical Windows 7 | `NOT_RUN` |

## Phase B local validation — 2026-07-31

- Full Core/Data test project, Release x86/net10: `843/843 PASS`.
- Focused disk-cache/scope/catalog transition set: `38/38 PASS`.
- WPF imaging, Release x86/net48: `19/19 PASS`.
- WPF application, UiSmokeHarness and complete solution Release builds:
  `PASS`, zero warnings and zero errors.
- Product-image UI smoke at `1024x768`: `PASS`; list virtualization, stable
  row height, no-image state, editor commands, accessibility labels and
  IT/EN/ES/ZH resources verified.
- Run-scoped profile smoke: `PASS`; DPAPI isolation, shared-profile
  immutability, no plaintext secrets, no offline-authority clone and exact
  local cleanup verified.
- Locked restore: `PASS` for all eight solution projects.
- Required source gates: `46/46 PASS`.
- Focused fail-closed supply-chain negative vectors: `20 PASS`.
- Static product-image Phase B gate and `git diff --check`: `PASS`.
- Independent final review after cache transition/removal race fixes:
  `P0/P1/P2/P3 = 0/0/0/0`.
- Local pgTAP container validation of the Admin boundary remains `NOT_RUN`:
  this non-elevated machine cannot complete Docker Desktop installation and
  firmware virtualization is disabled. The database test is delegated to the
  Admin CI gate; no PASS is inferred locally.

Redacted artifacts are stored outside the repositories under the execution
evidence root. They include TRX results and UI screenshots but no credentials,
signed URLs, Storage paths or request bodies.

## Redaction rules

Evidence outside repositories may contain only run HMAC/digests, counts,
bounded safe codes and timestamps. It must never contain raw run markers,
credentials, DPAPI blobs, signed URLs, Storage paths, private local file paths,
request bodies, real product data or exact private manifests.
