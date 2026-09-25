# Functional and performance completion — 2026-09-25

## Execution identity and limits

- Repository: `XNIW/Win7POS`, `C:\Dev\Win7POS`, origin `https://github.com/XNIW/Win7POS.git`.
- Initial fetch: local `main` and `origin/main` both `10e0b06fc84887c54308ea87b314e714fcb86cae`; staged/unstaged/untracked empty; ahead/behind 0/0.
- Work branch: `codex/functional-performance-completion-20260925`. Existing worktrees were inventoried and left untouched.
- Baseline measurement checkout: `C:\Dev\Win7POS-functional-baseline-20260925`, detached initial SHA, with opt-in counters and the same benchmark harness only.
- Host: ASUS Zenbook 14 UX3405CA, Intel Core Ultra 7 255H (16 logical processors), 16,497,893,376 bytes RAM; Windows 11 Home Single Language 10.0.26200 x64.
- SDK: exactly 10.0.301 from `C:\Dev\dotnet10`; system PATH SDKs 8/9 cannot satisfy global.json. Shipped WPF remains net48/x86.
- Evidence root: `C:\Dev\Win7POS-evidence-20260925`. Every accepted harness scenario runs in a separate process/data directory. No operational database, hardware output, production configuration or secrets were modified.
- Open PRs at intake: Dependabot #100, #101, #106, #107, #108; none is part of this work.
- Historical decomposition/image/hardware status was reconciled against `WIN7POS_POST_MERGE_COMPLETION_LEDGER.md`, not reopened from stale roadmap labels. Previous physical acceptance/defer is not evidence for this change.

Final commit/PR/CI/package attestations are recorded in the delivery evidence, avoiding a self-referential documentation SHA.

## Baseline and reproduction

Locked restore, solution Release build (zero warnings/errors), 48/48 required gates and CLI selftest passed. The first Core/Data run passed 1,010/1,011; its architecture scan raced a concurrent WPF build and found a disappearing `*_wpftmp.csproj`. The architecture class passed on immediate serial rerun. Subsequent builds and test suites are serialized; this infrastructure failure is retained in `baseline-tests.log`.

The new Core/Data repro suite on unchanged production source failed 11/17 cases (new/existing/manual cart discounts, fixed/final discounts, limits, overflow, exact CLP and literal LIKE). WPF reproduction separately failed export timeout and injected mid-save printer rollback. Logs: `repro-core.log`, `baseline-regressions/functional-completion.txt`.

## Findings and closure criteria

| ID | Priority / impact | Reachable path and original result | Correction and regression criterion |
| --- | --- | --- | --- |
| W7-F01 | P1; service indefinitely blocked | Public `ExportDailyCsvAsync` reacquired its own semaphore. UI daily export calls `GetDailyCsvContentAsync` directly; no current UI caller of legacy file-export wrapper was found. The public compatibility wrapper is retained and fixed. | Gate-owning public methods use a private no-lock CSV helper; two exports, cancellation, exceptions and subsequent snapshot/settings work must complete under process timeout. |
| W7-F02 | P1; wrong/negative sales and overflow | Scan/manual additions did not recalculate; fixed discounts survived price/quantity reduction; increments bypassed 100,000; final unit price became an aggregate; double rounded large CLP incorrectly. | One recalculation path, checked gross arithmetic, decimal percentage half-away rounding, stable final-unit metadata, validated restore. `PosSessionEconomicsTests`, workflow and actual sale/receipt/outbox smoke. |
| W7-F03 | P1; scan latency and dispatcher stalls | Every row opened a connection/query; refresh repeated item lookups; positional keys could retarget an edit. | Existing 900-key batch, ordinal deduplication, read transaction, indexed discounts, stable per-line keys. Workflow gate is acquired before scheduling DB work. Runtime counters assert constant queries and zero product queries on dispatcher; stale keys/snapshots are rejected. |
| W7-F04 | P2; product not found | Search by name `70%`/`ART_01` failed; exact barcode could hide the defect. | Shared SQLite `LIKE ... ESCAPE '!'`, escape `!` first. Name, exact barcode priority, %, _, !, backslash, brackets, apostrophe, spaces, Chinese, empty result and count/page tests. |
| W7-F05 | P1; lost carts / duplicate consumption | Second-precision IDs collided; memory-only recovery deleted durable source and overwrote active cart. | Timestamp+GUID identity; retain hold until atomic sale/stock/outbox consumption; persistent sale-code claim; product identity metadata; shop/epoch/restore binding; active-cart protection; retry, restart, disabled product, foreign-shop and injected consumption rollback tests. |
| W7-F06 | P2; wrong preview/removal | Late A could replace selected B; delete removed the selection after await. | Capture requested row and generation; invalidate close/refresh; reject concurrent mutations; deterministic reverse completion, selection-during-delete, double click and close tests. SalesRegister/DailyReport already guard preview generations. UserManagement repository reads currently complete synchronously; no late-response reproduction there. |
| W7-F07 | P1; inconsistent printer/cash-drawer configuration | 15 independent key writes; trigger on legacy key left new values committed. | Validate copied settings, write all keys in one transaction, read one snapshot; fault injection and concurrent reader test. Existing hardware gates remain unchanged. |
| W7-F08 | P2; preview/receipt inconsistency and retention | Discount preview used integer truncation; fixed/final line discounts were omitted by the legacy formatter; anonymous localization subscription retained closed discount view models. | Same CLP rounding in preview, exact line total shown, formatter recognizes all line adjustments, dispose subscription; weak-reference WPF regression. |
| W7-F09 | P2; stale cart presentation | Older asynchronous cart snapshot could overwrite a newer one; quantity dialog resolved the selection at completion. | Monotonic workflow snapshot revision and captured original line key; reverse snapshot application and stale-key runtime tests. |
| W7-F10 | P1; duplicate economics after ambiguous commit | Runtime fault at `after_commit_before_return`, then adding 50 to the already committed 100 cart produced 150. | Resolve the pending code and exact content before any cart mutation; a committed cart is cleared, uncommitted edits acquire a new attempt. Integrated WPF regression proves final persisted sum 800 (650 + 100 + 50), with no repeated stock/outbox effect. Repro: `repro-lost-result-data/functional-completion.txt`. |
| W7-F11 | P2; unavailable recovery actions | Existing visual harness failed `copyWithin=False` when catalog error details grew beyond the owner's height; footer was clipped. | The content row uses remaining height while title/footer retain natural size; `SizeToContent` remains. Existing runtime observability assertions pass in four languages, collapsed/expanded, plus compact initial login. |
| W7-F12 | P2; rendered scan latency | The 500-line view invalidated 22 bindings per unchanged row on every scan; SQL batching alone left rendered-view p95 near 756 ms. | Unchanged rows and unchanged selection no longer notify. A runtime notification regression and identical before/after bitmap-render benchmark verify the improvement. |

W7-F01 through W7-F12 are `FIXED_AND_VERIFIED` by the targeted tests and runtime matrix described below. F05 also rejects deletion from a stale selection after a shop transition; the foreign cart is preserved.

## Economic and recovery semantics

Existing percentage policy is **additive on gross**: item 1,000, line 20%, cart 10% gives 700, not 720. A line discount is bounded by its gross; the cart adjustment is bounded by remaining payable gross. This preserves existing ordinary combinations and prevents negative ordinary sales. Fixed amounts clamp down when the line shrinks. Final-unit discounts retain their requested unit price through quantity/price changes. CLP remains integer; percentage discount amounts round half away from zero at the line/cart aggregate, and the dialog additionally shows the exact line total.

Held rows, product identity metadata, scope and sale claim are committed together using existing `app_settings` and held tables; no schema migration or runtime dependency was added. Recovery does not reserve stock or execute a sale. Prices/discounts are the held snapshot, while active product identity is revalidated. Re-suspension updates the same durable hold. Clearing an active recovered cart preserves the original hold. The sale transaction consumes it only after sale/lines/stock/outbox writes succeed. A retry uses the same code and existing exact-replay protection.

Legacy timestamp-only IDs remain readable. An unbound legacy hold can be recovered only in an unbound database with no shop transition/restore; ambiguous legacy/foreign/restore scopes remain preserved and cannot enter a new shop's sale. This is a deliberate fail-closed recovery restriction, not a claim that old carts have proven shop identity.

## Functional matrix

| Flow / UI entry | Contract and permission | Local/remote and recovery boundary | Evidence to execute |
| --- | --- | --- | --- |
| First start / Access POS | Unified login, explicit local recovery only for availability failure | Admin Web first-login; DPAPI trusted/offline lease; valid denial never enables fallback | startup/access gates; authorization WPF suite; staging first-login |
| Offline login / operator switch | Current staff/permission and lease at commit | Local mirror plus authoritative attestation; revocation/generation fencing | authorization WPF suite, remote-mirror tests |
| Catalog check / enter POS | Sale-safe catalog required, recovery limited to catalog actions | Initial full drain then bounded supervisor delta; incomplete/denied cannot sell | catalog safety/exactness tests, startup/catalog gates |
| Scan / search / edit product | `pos.sell`, catalog view/edit/price edit | SQLite first; manual catalog changes go through existing outbox | literal search tests, cart workflow, 20k/100k benchmark |
| Quantity / discount / payment | `pos.discount`, override limits, `pos.pay` at commit | Integer CLP, persist-first sale+stock+outbox | economics tests; integrated WPF sale/retry; authorization tests |
| Suspend / recover | `pos.suspend_cart`, `pos.recover_cart` | Local durable hold, scope check, atomic consumption | isolated WPF cases and transaction rollback/replay test |
| Returns / void | `pos.refund`, `pos.void_sale` | Original-line binding, cumulative quantity/economics, reversal dependency | SaleReversalWriter/ReversalEconomics tests and required gates |
| Receipt / reprint | `pos.reprint_receipt`; explicit print action | Local immutable receipt snapshot; no fiscal service/terminal integration implied | receipt renderer tests, WPF receipt alignment; physical output not performed |
| Register / daily report / CSV | own/all register and daily-close permissions | Local saved sales; read snapshot and save-dialog CSV | sale read tests, daily export concurrency/cancellation, integrated register check |
| Supplier Excel / categories / suppliers | catalog import/edit | Shared import contract, bounded analysis/apply, dirty data rejection, catalog outbox | current Android parity, streaming bounds, batch/import tests |
| Images | existing catalog/image permissions | Bounded decode/cache, offline durable operations, supervisor lane | image Core/Data and net48 profile smokes, staging acceptance |
| Printer settings | `settings.printer` | Atomic local group including legacy keys; hardware fallback gates retained | SettingsAtomicTests; injected WPF failure; printer safety gates |
| Backup / restore | `db.backup`, `db.restore` | Verified snapshot, pre-backup, durable replace/recovery, shop/outbox fence | migration/restore tests; CLI backup failure/performance harness |
| Automatic sync / restart | trusted generation, lease and policy | Single supervisor; idempotent outbox, backoff, independent lanes | supervisor/outbox/generation suites, authorization restart smoke, staging |

Card is a locally recorded payment method. Remote fiscal emission and a bank-terminal integration are not implemented or asserted by this work. Physical Windows 7, printer, drawer, scanner, dual monitor and physical DPI qualification remain separate external tests.

## Performance protocol

Release net48/x86 on the host above, synthetic 20,000 and 100,000 product databases, ten historical prices per product (200k/1m history rows), stock 100,000, carts 1/10/50/100/500. The same `CartPerformanceSmoke` source is compiled against baseline and changed source; baseline changes are opt-in counters only. Raw CSV has first-call sample 0 and 30 warm samples; p50 is nearest-rank 15/30 and p95 is nearest-rank 29/30. First-call is not OS-cold startup. Counters count product SQL commands and opened policy-verified connections; PRAGMA statements are not mislabelled as product queries. Memory/GC and dispatcher probe latency are retained per sample.

For final acceptance, existing paging p95 budget stays 500 ms. Additional local scan budgets: p95 <= 50 ms at 500 lines and dispatcher probe p95 <= 16 ms; structural bound is one barcode lookup plus ceil(distinct product barcodes/900) detail queries and one concurrent workflow worker. These thresholds apply to the final serial rerun; they do not describe network or physical scanner latency. Small-cart thread-switch overhead must be disclosed in absolute milliseconds.

Final serial measurements use `before-complete-{20000,100000}` and `after-optimized-{20000,100000}` under the evidence root. The eight accepted CSVs are also committed in `docs/reports/evidence/2026-09-25-functional-performance/`. Earlier runs are retained externally, including an invalid idle/frame benchmark that depended on desktop compositor availability; those timings are not used below.

### Scan service, p50 / p95 milliseconds (30 warm samples)

| Products | Lines | Before | After | Product commands/connections before → after |
| --- | --- | --- | --- | --- |
| 20k | 1 | 0.515 / 0.669 | 0.780 / 1.173 | 2 → 2 |
| 20k | 10 | 1.843 / 3.644 | 0.826 / 1.129 | 11 → 2 |
| 20k | 50 | 7.832 / 9.360 | 1.150 / 1.501 | 51 → 2 |
| 20k | 100 | 15.806 / 16.915 | 1.511 / 2.083 | 101 → 2 |
| 20k | 500 | 89.561 / 93.597 | 6.155 / 7.315 | 501 → 2 |
| 100k | 1 | 0.550 / 1.168 | 0.798 / 1.015 | 2 → 2 |
| 100k | 10 | 1.803 / 2.916 | 0.866 / 1.058 | 11 → 2 |
| 100k | 50 | 8.410 / 10.156 | 1.150 / 1.804 | 51 → 2 |
| 100k | 100 | 15.873 / 17.581 | 1.528 / 2.126 | 101 → 2 |
| 100k | 500 | 92.396 / 101.256 | 6.104 / 7.739 | 501 → 2 |

At 500 lines, dispatcher probe p95 falls from 93.695 to 0.104 ms (20k) and 101.367 to 0.130 ms (100k). All product commands moved off the calling dispatcher. Final service max at 500 lines: 9.182/10.024 ms. Small-cart p50 increases by about 0.25 ms from the explicit thread hop; this cost is retained to keep SQLite off UI. Scan-stage private-memory maxima remain approximately 34 MiB.

### Other local flows

| Operation | 20k before p50/p95 → after | 100k before p50/p95 → after |
| --- | --- | --- |
| Scan + actual POS binding/layout + 1024x768 bitmap render, 500 lines | 655.994/786.633 → 175.151/290.301 ms | 660.446/757.849 → 174.767/320.095 ms |
| Search details, first 100 matches | 6.046/7.808 → 6.167/8.508 ms | 34.747/37.761 → 34.690/38.128 ms |
| Sale+stock+outbox local transaction, no network/authority UI | 5.183/6.207 → 5.511/6.987 ms | 5.641/7.405 → 5.286/7.309 ms |
| CSV content, growing 1–31-sale fixture | 0.474/0.611 → 2.863/6.240 ms | 0.491/0.569 → 2.918/5.555 ms |

The CSV increase is an explicit off-dispatcher scheduling cost; final maxima are 23.260/7.595 ms, while the formerly deadlocking file wrapper now terminates and releases its gate after write failure and cancellation. Search changes are small and preserve the existing paging budget.

The bitmap benchmark forces WPF rendering even on an occluded desktop; it is not monitor/scanner latency. Both revisions exhibit a large early renderer outlier, retained in the 30-sample distribution: before maxima 5,114.906/5,967.307 ms, after 5,274.952/5,135.100 ms. No claim of uniformly low frame latency is made. Render-stage private bytes peak at 184.8/182.9 MiB before and 212.6/212.7 MiB after; the latter settle near 208 MiB at the end, rather than increasing monotonically. Fewer forced binding updates also reduce GC collections over the 30 renders: 100k gen0/gen1/gen2 312/121/31 → 77/59/6. This short sample is not a long-duration leak qualification.

First creation/initialization/render-idle of the real POS view in the harness (one sample, not p95): 428.401 → 367.802 ms at 20k and 392.790 → 348.057 ms at 100k. These exclude process launch and fixture creation and are not an OS-cold startup claim. No system cache purge or policy modification was used.

### Import and backup/restore follow-through

The existing CLI benchmarks were executed serially against baseline and changed source, with SDK/runtime 10.0.301/10.0 x64, Release. They are a separate managed-runtime profile from WPF net48/x86; comparisons are within that identical CLI profile. Supplier input is synthetic XLSX, 5,000 rows, 20k/100k existing products, one warmup plus three measured repetitions. Median worksheet read changes 219.99 → 209.06 ms (20k), 211.53 → 190.04 ms (100k); targeted lookup 79.12 → 75.04 and 78.16 → 71.84 ms. Dry-run apply changes 158.06 → 165.25 and 151.43 → 137.27 ms. The actual apply boundary preserves identical application fingerprint `575cbf47c62ab4f50a47f384a674b267e1f82d7fdd72f0a471a28163fa97eaed`; at 100k it inserts 1,001 and updates 3,997 products in 10 lookup batches, with 9,996 history rows. This fixture intentionally measures local apply without remote outbox delivery. Logs: `{before,after}-import-{20000,100000}.log` in the evidence root.

Backup/restore uses the existing concurrent-writer/WAL-safe benchmark, 32/128 MiB, one warmup and three measured repetitions. Median backup before → after: 158.677 → 185.416 ms (32 MiB), 649.233 → 626.752 ms (128 MiB). Median restore: 517.638 → 444.800 ms and 1,509.798 → 1,459.588 ms. All fingerprints and committed WAL frames are preserved. These small samples are medians, not p95, and the 32 MiB backup increase is disclosed without attributing noisy disk timings to code that did not change. Fault matrix passes 18 restore points, 9 cancellation points, 3 crash recovery points, 10 backup points and startup recovery; no partial backup/restore residue. Logs: `{before,after}-backup-perf.log`, `final-backup-failure.log`.

A separate command to launch/close production POS executables for a process-start comparison in copied test profiles was rejected by automatic approval review with only `blocked by policy`. It was not executed or retried through another mechanism. Thus process-to-usable-login cold/warm timing remains unmeasured; the view-entry measurements above must not be substituted for it. Remote sync/backlog and image transfer remain functional/structural acceptance evidence, not a claimed network p50/p95 benchmark.

## Validation record

Canonical local validation (`validation-summary.txt`): locked restore, Release solution, WPF/harness x86, 48/48 gates, **1,033/1,033 Core/Data tests, zero skipped**, CLI selftest, product-image profile/net48 serialization, authorization lease/restart, bounded logging and 100k product paging all passed. The integrated fixture uses a test-only trusted offline identity and injects transaction failure; no payment adapter, spooler or cash drawer is invoked.

Seven isolated functional processes cover F01/F02/F03/F05/F06/F07/F08/F09/F10, with deterministic faults, reversal of completion order, cancellation, restart and foreign-shop rejection. The existing full visual harness covers responsive POS at five sizes, keyboard focus, register/refund/daily receipt surfaces, all supplier-import steps and settings. It initially reproduced F11, then passed 48 required screenshots and all four-language recovery checks. The new regression script runs this visual suite too and additionally captures discount aggregate rounding and durable held carts.

The UI harness creates an owner of 680x560 DIPs for its error dialog; historical screenshot filenames say `1024x768` but are not proof of a physical 1024x768 display. WPF offscreen/captured layout, dispatcher and focus tests are software evidence. Physical Windows 7, real scanner input, printer/drawer output, monitor transitions and OS DPI changes were not executed.

Diff review checked authorization at commit, exact replay before held consumption, rollback of held metadata with sale/stock/outbox, scope checks for recovery and deletion, unchanged remote outbox schema, safe printer defaults, cancellation/finally gate release, and no general dependency/schema/framework upgrade. No cache was introduced for stock/catalog validity.

The new exact-code sale lookup delegates to the existing bounded receipt read, including the pre-materialization size guard. Its method is explicitly included in the existing read-boundary allowlist; no write-boundary checks were relaxed. The parity/oversized receipt tests cover it. The final serial Core/Data rerun again passed 1,033/1,033 with zero skipped; required gates returned 48/48 and the post-commit-response authorization smoke passed.

Remote CI and candidate validation bind to the delivered commit via GitHub run head SHA and release manifest; the final local attestation is outside the repository to avoid a cycle of documentation-only SHA commits.
