# Win7POS external validation backlog

## Status and scope

- Backlog status: `OPEN`.
- External validation status: `DEFERRED_EXTERNAL_VALIDATION`.
- Software merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production/hardware certification: `OPEN` and `NOT_YET_CERTIFIED`.

No item in this backlog is declared PASS. Every item is non-blocking for the
owner-authorized software merge and remains mandatory before production or
hardware certification.

| # | External validation item | Status | PASS declared | Software merge gate | Production/hardware certification |
| ---: | --- | --- | --- | --- | --- |
| 1 | Staging catalog full iniziale. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 2 | Staging incremental create/update/price/stock/tombstone. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 3 | Resume e network recovery reali. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 4 | Vendite cash/card/mixed. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 5 | Offline/reconnect e idempotenza server. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 6 | Refund e void reali. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 7 | Riconciliazione giornaliera. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 8 | BusinessDate/mezzanotte. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 9 | Windows 7 SP1 fisico. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 10 | Installer smoke Win7. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 11 | Dual-monitor Windows Extend. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 12 | Customer display fisico. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 13 | Scanner. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 14 | Xprinter. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 15 | Cash drawer. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |
| 16 | Profili DPI e IT/EN/ES/ZH runtime. | `DEFERRED_EXTERNAL_VALIDATION` | `NO` | `NON_BLOCKING_OWNER_AUTHORIZED` | `REQUIRED_BEFORE_CERTIFICATION` |

Completion evidence must come from the corresponding authenticated staging or
physical Win7/hardware execution. Static checks, synthetic fixtures, lifecycle
harnesses, local packaging and CI evidence do not change these statuses to PASS.
