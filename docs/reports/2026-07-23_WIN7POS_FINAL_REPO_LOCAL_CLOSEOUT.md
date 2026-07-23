# Win7POS final repository-local closeout — 2026-07-23

## Scope and honest status

This report records the completed repository-local closeout after the historical
2026-07-22 snapshot. It does not claim a production release, real signing,
staging deployment, authenticated staging E2E, migration repair, hardware E2E or
physical Windows 7 validation.

- Initial implementation baseline for this follow-up: `0c5052f3d32ead9a02b35367e554f918d4e44fd2`.
- Final repository-local implementation main: `93a5e4afa819f0de14513ddb7603091433d917ba`.
- Merge policy: every listed implementation PR used a normal merge commit; no
  squash, rebase merge, direct main commit or force push was used.
- Repository-local structural/P2 status: `DONE_MERGED`.
- Global status: `NOT_DONE` because the authoritative source-provenance/recovery
  reconciliation manifest and verified backup for the eight remote-only Supabase
  staging migrations are not available.

## Repository-local delivery ledger

| Work item | PR(s) | Exact head(s) | Normal merge(s) | Acceptance evidence |
| --- | --- | --- | --- | --- |
| SQLITE-DURABILITY-001 | [#25](https://github.com/XNIW/Win7POS/pull/25) | `ed5fc91` | `3887262` | DELETE/FULL/foreign-key/busy/temp/cache durability policy and regressions; exact-head and post-merge workflows completed successfully. |
| ARCH-005 | [#26](https://github.com/XNIW/Win7POS/pull/26) | `53b648b` | `56b3803` | Catalog sale-safety policy extraction; exact-head and post-merge workflows completed successfully. |
| PR-C | [#27](https://github.com/XNIW/Win7POS/pull/27) | `c9c973f` | `d675157` | Startup coordinator extraction; exact-head and post-merge workflows completed successfully. |
| PR-E / ARCH-003 | [#28](https://github.com/XNIW/Win7POS/pull/28)-[#31](https://github.com/XNIW/Win7POS/pull/31) | `4776469`, `feceb02`, `950ebd6`, `7d09139` | `6556a85`, `f2175ab`, `bc52904`, `38cf284` | Façade-preserving product query, price-history, local-write and remote-product-write extractions. |
| PR-F | [#32](https://github.com/XNIW/Win7POS/pull/32)-[#38](https://github.com/XNIW/Win7POS/pull/38) | `767662b`, `83a2515`, `26d6cc0`, `2f6baa1`, `d01b8b0`, `e7aed30`, `e862db4` | `0918437`, `0c95204`, `e2d8e24`, `93100e2`, `8bcbaea`, `f20030c`, `cc9f02c` | Read, line, stock, outbox, reversal and transaction writer extractions; caller-owned transaction and façade invariants preserved. |
| PERF-05 | [#39](https://github.com/XNIW/Win7POS/pull/39) | `777931aba66727779480fd6774d8c5c2548d5a3a` | `93a5e4afa819f0de14513ddb7603091433d917ba` | Commit-published remote-price SQL diagnostics, exact fresh-price accounting and rollback non-publication coverage. |

For PERF-05, exact-head CI/Security Supply Chain/Release Pack runs were
`30005059126` / `30005059146` / `30005117027`. Post-merge CI/Security Supply
Chain/Release Pack runs were `30005966795` / `30005966771` / `30005966770` on
`93a5e4a`; all completed successfully. The prior implementation PRs retain their
individual run records. Post-merge CI on PR #33 merge `0c952047` failed in run
`29985031154`; PR #34 remediated that failure and completed its post-merge
workflows successfully.

## PERF-05 evidence

- `CatalogApplyRunDiagnostics.RemotePriceApply` is an observational public
  aggregate. Each catalog page creates a fresh accumulator and merges it only
  after `tx.Commit()` succeeds.
- The deterministic fresh-price fixture has three valid prices across two pages:
  `5N + 2P = 19` SQL commands and `6N + 2P = 22` SQL statements.
- A deliberate ownership-write failure proves that a rolled-back page publishes
  no price diagnostic. A committed one-price page reports `7/8` and a following
  failed page leaves that aggregate unchanged.
- Local validation on the exact PR head: Core/Data `610/610`, required gates
  `44/44`, WPF and smoke-harness Release `net48/x86` builds with zero warnings
  or errors, CLI self-test, Gitleaks worktree/history (424 commits),
  `batch-price-only 3 1 2` (19/22), and 19,763-row paged-full exactness
  `Verified` in 3/3 iterations.
- The local Windows Application Control policy blocked launching the compiled
  WPF smoke harness, so it was not bypassed. The exact-head and post-merge CI
  runners completed both WPF smoke checks successfully.

## External-state inventory and blocker

The Admin repository was observed at `XNIW/merchandise-control-admin-web` main
`9406da338691e70e627c26867122499f944de897`. The migration inventory is factual,
not a migration plan:

| Inventory | Count / IDs |
| --- | --- |
| Source migration IDs | 72 |
| Applied staging history IDs | 79 |
| Common IDs | 71 |
| Remote-only Supabase staging IDs | `20260707183000`, `20260707200500`, `20260708003000`, `20260713010000`, `20260713020000`, `20260718120000`, `20260718235345`, `20260719090000` |
| Source-only ID | `20260719170600` |

No authoritative source-provenance/recovery reconciliation manifest and verified
backup for the eight remote-only Supabase staging migrations was supplied; no
migration was repaired, rebased, baselined, reverted, or deployed in this
execution.

The required owner action is to provide the authoritative
source-provenance/recovery reconciliation manifest and verified backup for the
eight remote-only Supabase staging migrations.

## Explicitly unclaimed work

| Area | Status |
| --- | --- |
| P1-REL-01 real signing / protected tag / RFC3161 | `PARTIAL_EXTERNAL_SIGNING` |
| Admin staging deployment and migration reconciliation | `BLOCKED_EXTERNAL` |
| Authenticated staging E2E and public download validation | Not executed |
| Physical Windows 7 | `DEFERRED_BY_USER` |
| Hardware E2E | Not executed in this closeout |

No credential, token, production data or external migration mutation is recorded
in this report.
