# Win7POS post-merge completion ledger

## Source and status rules

- Authoritative source main: `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`.
- Software merge: `DONE_SOFTWARE_MERGED`; PR `#4` is already merged and no old
  feature branch is to be merged again.
- Final main CI: run `29591597390`, `completed/success` on `f3e779b`.
- Final main Release Pack: run `29591597131`, `completed/success` on `f3e779b`.
- Production certification: `OPEN` until all 16 external items have real PASS
  evidence.
- A structural item becomes `DONE_MERGED` only after an explicit later merge.
  Publishing a green PR leaves it `READY_FOR_REVIEW`.

## A. External certification

Overall status: `OPEN`; PASS count: `0/16`.

| # | Certification item | Status | Blocking evidence |
| ---: | --- | --- | --- |
| 1 | Staging catalog full initial bootstrap | `BLOCKED_CREDENTIALS` | No authorized QA shop/staff credential was supplied. |
| 2 | Staging incremental create/update/name/price/stock/category/supplier/tombstone | `BLOCKED_CREDENTIALS` | Authenticated QA mutation is unavailable. |
| 3 | Staging resume and real network recovery | `BLOCKED_CREDENTIALS` | Authenticated QA session is unavailable. |
| 4 | Staging cash/card/mixed sales | `BLOCKED_CREDENTIALS` | Authorized QA cashier/shop session is unavailable. |
| 5 | Staging offline/reconnect, retry and duplicate ACK | `BLOCKED_CREDENTIALS` | Server-side QA evidence cannot be obtained without a QA session. |
| 6 | Staging partial refund and full void | `BLOCKED_CREDENTIALS` | Authorized QA sales/reversal flow is unavailable. |
| 7 | Staging daily reconciliation | `BLOCKED_CREDENTIALS` | Local/server QA comparison is unavailable. |
| 8 | Staging businessDate and midnight boundary | `BLOCKED_CREDENTIALS` | Authenticated controlled-time QA evidence is unavailable. |
| 9 | Physical Windows 7 SP1 | `BLOCKED_WIN7` | Available host is not Windows 7 SP1. |
| 10 | GitHub installer smoke on Windows 7 | `BLOCKED_WIN7` | No physical/VM Windows 7 certification target is available. |
| 11 | Dual-monitor Windows Extend | `BLOCKED_HARDWARE` | Available host has no qualifying two-monitor topology. |
| 12 | Physical customer display | `BLOCKED_HARDWARE` | No qualifying customer monitor/hot-plug setup is available. |
| 13 | Scanner | `BLOCKED_HARDWARE` | No scanner is attached. |
| 14 | Xprinter | `BLOCKED_HARDWARE` | No Xprinter is attached. |
| 15 | Cash drawer | `BLOCKED_HARDWARE` | No cash drawer is attached. |
| 16 | Runtime DPI and IT/EN/ES/ZH matrix | `NOT_RUN` | Required Win7/display/authenticated surfaces are unavailable. |

Static checks, tests, synthetic databases, CI, local packaging and fake monitor
topologies cannot change any row to PASS. The authoritative external backlog is
updated only when a real PASS is obtained.

## B-H. Structural hardening sequence

| Order | Structural item | PR | Status | Evidence / next action |
| --- | --- | --- | --- | --- |
| B | Persistence foundation | PR-A / GitHub `#5` | `READY_FOR_REVIEW` | Branch `codex/pr-a-persistence-foundation-20260717-114614`; do not auto-merge. |
| C | Versioned migrations | PR-B | `NOT_STARTED` | Next incomplete PR after PR-A review/merge decision. |
| D | Startup coordinator | PR-C | `NOT_STARTED` | Wait for preceding item. |
| E | Catalog state-machine/performance split | PR-D | `NOT_STARTED` | Wait for preceding item. |
| F | ProductRepository split | PR-E | `NOT_STARTED` | Wait for preceding item. |
| G | SaleRepository split | PR-F | `NOT_STARTED` | Wait for preceding item. |
| H | Reproducible/signable release chain | PR-G | `NOT_STARTED` | Wait for preceding item; no certificate or secret in Git. |

Only one structural PR is selected in this execution. PR-B is recorded as the
next incomplete software item, but it is not implemented here.
