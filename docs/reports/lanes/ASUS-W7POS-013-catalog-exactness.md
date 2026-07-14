# ASUS-W7POS-013 — Lane B catalog exactness evidence

Date: 2026-07-14
Branch: `codex/asus-catalog-exactness`
Software commit: `8669774bc1013bc2eddcc09bf6a9bd7778bcd04a`

## Outcome

The Win7POS client now has a backward-compatible `catalogSummary` contract and an explicit `Verified` / `Unverified` / `Mismatch` state model. A full refresh can be `Verified` only after the final page, authoritative count agreement, a clean SQLite structural audit, stable shop binding, and comparable checksum evidence when the server supplies a checksum.

Legacy servers that omit `catalogSummary` remain compatible, but their completed refresh is persisted as `Unverified`; it is never represented as exactly verified. A mismatch or repair-required result atomically removes sale-safe/initial-completed markers and prevents them from being written again until repair.

## Task ledger

| Task | State | Commit | Evidence | Notes |
|---|---|---|---|---|
| ASUS-W7POS-013.B1 optional summary contract and compatibility validation | DONE | `8669774` | Contract round-trip and malformed-summary tests | No staging count is embedded in runtime or tests. |
| ASUS-W7POS-013.B2 full-refresh SQLite audit and exactness evaluator | DONE | `8669774` | Full suite: 102/102 passed | Covers duplicates, invalid product rows, missing metadata, orphans, inactive references, pending prices, tombstones, and non-authoritative active rows. |
| ASUS-W7POS-013.B3 shop-bound persistence and full-repair state | DONE | `8669774` | Exactness tests: 10/10 passed | Binding/epoch guarded; safe API and barrier-already-held API are tested separately. |
| ASUS-W7POS-013.B4 pull-service wiring | PLANNED | orchestrator-owned | API handoff sent to orchestrator | `PosCatalogPullService.cs` was intentionally not edited in this lane. |
| ASUS-W7POS-013.B5 Admin Console versus staging SQLite proof | BLOCKED_EXTERNAL | — | No credentials, redacted response, Admin screenshot, or staging DB was available in this lane | Must remain external until the authoritative runtime counts can actually be captured. |

## Contract and verification semantics

Optional response field:

```text
catalogSummary {
  products,
  activeProducts,
  categories,
  suppliers,
  prices,
  checksum,
  checksumAlgorithm
}
```

- Counts are nullable so omission by an older server is distinguishable from an authoritative zero.
- Negative counts, `activeProducts > products`, control characters, overlong checksum metadata, and an algorithm without a checksum are rejected by compatibility validation.
- Exact verification requires all five counts. `products`, `categories`, and `suppliers` are compared with distinct authoritative IDs received and reconciled local active rows; `activeProducts` is compared with local active remote products; `prices` is compared with the complete received-price evidence.
- `hasMore=true`, missing page/version/cursor evidence, inconsistent raw-row evidence, duplicates, invalid IDs, missing `product_meta`, invalid name/barcode/price, orphan/inactive references, pending prices, or non-authoritative active rows cannot produce `Verified`.
- A supplied checksum must be compared case-sensitively with a locally computed checksum under the same algorithm. Without a defined/computable canonical checksum it remains `Unverified`; a differing comparable checksum is `Mismatch`.
- Only cursor fingerprints and checksum fingerprints are persisted in diagnostics. No payload, token, PIN, credential, or URL is stored or logged by this implementation.

## Persisted non-sensitive evidence

The shop-state repository stores the bound shop, sync mode, catalog version, cursor fingerprint, pages, received entity counts, expected counts, active/distinct local counts, duplicate/invalid/meta/orphan/reference counts, pending prices, inactive/tombstone counts, non-authoritative active counts, duration, rows/sec, evaluation timestamp, verified timestamp, redacted status code, repair flag, and checksum fingerprints/algorithm.

`RequestFullRepairAsync(shopId, shopCode, epoch)` is the safe admin entry point and acquires `CatalogShopTransitionBarrier`. `RequestFullRepairWhileBarrierHeldAsync(shopId, shopCode, epoch)` is transaction-only for an orchestrator that already owns that barrier. Both preserve binding and epoch while atomically clearing cursor, sale-safe, and initial-completed state and setting `Unverified` plus repair-required.

## Validation

| Command | Result |
|---|---|
| `C:\Dev\dotnet10\dotnet.exe test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore` | PASS, 102/102 |
| focused `CatalogExactnessTests` | PASS, 10/10 |
| `C:\Dev\dotnet10\dotnet.exe build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS, 0 warnings / 0 errors; `net48`, `win-x86` |
| `pwsh -NoProfile -File scripts\check-pos-catalog-pull.ps1` | PASS |
| `pwsh -NoProfile -File scripts\check-pos-outbox-shop-binding.ps1` | PASS |
| `git diff --check` | PASS |
| changed-file staging-count and secret-literal scan | PASS |

## Admin Web contract gap / remaining runtime evidence

No redacted staging response was available, so this lane cannot claim whether Admin Web currently emits totals or a checksum. Admin Web must provide a complete authoritative summary on the final full-refresh page (or repeat a stable summary on all pages), define whether product/reference counts represent distinct active entities, and publish the checksum canonicalization and algorithm if checksum verification is expected.

The final staging proof still requires an authorized operator to capture a redacted Admin Console screenshot and run the implemented SQLite audit after a drained full refresh. Product, category, supplier, and price totals must come from that runtime evidence; duplicate, invalid, orphan, pending, and non-authoritative-active counts must be zero. Until that is executed, the staging exact-count claim remains `BLOCKED_EXTERNAL`, not PASS.
