# Win7POS post-merge completion ledger

## Source and status rules

- Previous software baseline: `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`;
  PR `#4` remains already merged and must not be merged again.
- PR-A software merge: `DONE_MERGED`; PR `#5` was integrated by fast-forward at
  `607e1f15fce64fed48e84e5fe680d40741ef6031`, without force push.
- PR-A main CI: run `29600645459`, `completed/success` on exact `607e1f1`.
- PR-A main Release Pack: run `29600645440`, `completed/success` on exact
  `607e1f1`; installer and release ZIP were downloaded and matched the embedded
  clean-main provenance.
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
| B | Persistence foundation | PR-A / GitHub `#5` | `DONE_MERGED` | Fast-forward head `607e1f1`; PR/main CI and Release Pack green on the exact SHA. |
| C | Versioned migrations | PR-B | `READY_FOR_REVIEW` | Six immutable checksummed migrations, verified legacy bootstrap/backup/rollback and six sanitized fixtures; publish without automatic merge. |
| D | Startup coordinator | PR-C | `WAITING` | Wait for PR-B review and explicit merge decision. |
| E | Catalog state-machine/performance split | PR-D | `NOT_STARTED` | Wait for preceding item. |
| F | ProductRepository split | PR-E | `NOT_STARTED` | Wait for preceding item. |
| G | SaleRepository split | PR-F | `NOT_STARTED` | Wait for preceding item. |
| H | Reproducible/signable release chain | PR-G | `NOT_STARTED` | Wait for preceding item; no certificate or secret in Git. |

PR-A remains `DONE_MERGED`. PR-B is implemented on its independent branch and is
left `READY_FOR_REVIEW`; this status does not imply merge. PR-C remains
`WAITING`. External certification remains `OPEN 0/16`.
