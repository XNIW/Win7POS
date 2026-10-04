# WIN7POS_FUNCTIONAL_SYNC_OPERATIONS_CLOSEOUT_RESULT

Repository-local closeout, 2026-10-03; external acceptance remains open.
This is the single report for this execution;
machine receipts live in `evidence/2026-10-03-closeout/`. Historical reports and
owner decisions remain unchanged. No external acceptance is inferred from builds.
The residual execution of 2026-10-04 is recorded in the final section below;
its current machine receipt supersedes earlier acceptance snapshots without
changing historical failures or the contract-based C01–C12 matrix.

## Revision and protection

Initial Win7POS `HEAD` and fetched `origin/main`:
`42afc8817422a5d1c3ed06e0ca50f7fb44a3207b`, clean, ahead/behind 0/0.
Branch: `codex/functional-sync-operations-closeout-20261003`.
Qualified software main after B/A/C:
`8a25bd217362b3143ff1045b110b3fc9c9c6f7fc`. Package D uses
`codex/closeout-evidence-release-20261003` and changes documentation, redacted
evidence and failure-only output in the existing functional runner. Product
src/tests/workflow trees remain unchanged. Its final merge SHA and delivery results are
recorded after integration in the machine receipt described below.
Initial checkpoint: `C:\Dev\_codex-evidence\win7pos-closeout-20261003\checkpoint.json`.
There were no local staged/unstaged/untracked files or unpublished commits before
branch creation. Existing unrelated worktrees and image checkpoints are preserved.

| Repository | Fetched main | Local checkout / active change |
| --- | --- | --- |
| Win7POS | `42afc8817422a5d1c3ed06e0ca50f7fb44a3207b` | Clean initial main; this execution branch |
| Admin | `553c4568b335ee95be31aaaea6735ed0a751879c` | `2f9890c2`, clean; not updated or mutated |
| Android | `04f6fe26a9b8825f3ffc6d3fcb7e014221880a75` | `4b2b4a93`, clean; not updated or mutated |
| iOS | `8dfbf9a033c1e9be05c941e1cb49712137c893bc` | Mac owner controls checkout |

Android PR14 and iOS PR14 are merged at their fetched main revisions. Admin
PR128 (`7232637b6e2750fd4c3f90d25fdbd1ca16189383`) and dependent PR130
(`d4f171d4b98c483a731a7cb936acd1bc2ce7c1fa`) are OPEN with green source CI;
deployment jobs are skipped. This is source evidence, not database/deployment
readiness. The coordinator reports TEST registry155 and preserved SQL metadata;
this execution has performed no remote SQL, deploy, probe or cleanup.

## Execution matrix

| ID/package | Requirement / current code | Existing evidence | Actual residual / files / acceptance |
| --- | --- | --- | --- |
| A/C01–C12 | Generation-scoped supervisor, durable catalog/article/import/sales/image lanes, auth fences | Existing Core/Data tests and two-process article harness | Authenticated four-platform proof needs fresh common readiness, exact release binding and installed-client receipts; use existing harness and contract projections |
| B/BUSY | `SqliteOnlineBackup` bounded retry and verified publication already shipped | Writer and fault tests; historical successful reruns | Make actual provider lock and writer synchronization deterministic; finite targeted runs, no journaling change |
| C/H1 | W7-F07 already atomically saves/reads printer/drawer legacy keys | `SettingsAtomicTests`, F07 WPF scenario | Typed scanner/receipt/drawer, audited save, isolated scanner test, selected-queue diagnostics and truthful health |
| C/H2 | OPERATIONS-1 schedule/retention/destination/store already complete | Existing 98-case backup suite | Portable allowlisted profile, bounded strict parser, diff/confirm, atomic import/reset/audit, service permissions, observed-version audit |
| C/H3 | Atomic display repository, topology, no-focus projection already present | Display projection/layout/monitor tests | Managed logo, idle/privacy options, unsaved non-activating preview and finite sale-free test pattern |
| D | net48/x86 app, netstandard2.0 Core/Data, pinned SDK10.0.301 | Canonical CI/Security/ReleasePack and PR112 historical receipts | Serial final gates/suite/build, independent diff reviews, ordinary PR/merge, exact-main pack verification |

One writer per package. No existing sync engine, OPERATIONS-1 or PR14 work is
reimplemented. `global.json` is unchanged; SDK10.0.301 is available at
`C:\Dev\.dotnet-sdk-10.0.301\dotnet.exe`.

## Readiness and external acceptance

Host: Asus Windows11 Home Single Language, build26300, 64-bit; WPF candidate
remains net48/x86. No physical Windows7 target or peripheral has been established
for this execution. No physical print or drawer pulse is authorized by inventory.
The user subsequently confirmed that the Asus desktop is available and that
Win7/share TEST are unavailable. A one-shot Windows Task Scheduler launch using
the already logged-on user, Interactive/Limited, reached the real input desktop.
It used no password/elevation/recurring trigger or observer change. All three owned
tasks were removed after terminal completion; their failures are retained below.

The user-designated TEST shop matches the existing `asus-staging` DPAPI vault
when compared in memory. Version2/DPAPI/ACL validation passes; no credential was
exported. Endpoint is the canonical public staging Workers hostname. At Admin
`553c456`, `docs/HANDOFFS/WIN7POS_ARTICLE_ACCEPTANCE_READY.json` is absent.
The canonical runner requires fresh maintainer readiness, scope/profile binding,
clean exact-main checkout and an exact-SHA successful ReleasePack. These gates
remain in force.

The user authorized coordination with the active Mac chat **Riprendi collaudo
ecosistema POS**. Its receipt says native recovery/checkpoint STEP1 is active,
caller/window readiness is being prepared, no current common/POS readiness
exists, and Android TEST `3ccf651e` / iOS `2783283d` are saved builds rather than
attested installed authenticated clients. POS is tracked as cross-consumer
residual by that coordinator. SQL application, Git merge and deployment are
separate facts; only the owning coordinator controls backend and mobile writers.

All supported authenticated C01–C12 directions are currently `BLOCKED` by this
fresh readiness/artifact requirement. Unsupported domain directions are
`NOT_APPLICABLE` after checking the source contracts below. Core/Data, loopback and
UI fixtures are separate local proofs. Equal counts, HTTP200 or empty outbox
will not close any live row. The required open/recover/edit/propagate/reopen/edit
again sequence has **not been executed in this run**.

| Case | Required live comparison | POS ↔ Admin | Android ↔ Admin | iOS ↔ Admin | Cross-consumer direction |
| --- | --- | --- | --- | --- | --- |
| C01 | Authorized bootstrap and final recovery | BLOCKED | BLOCKED | BLOCKED | Admin → POS/Android/iOS: BLOCKED |
| C02 | CRUD, references, prices and supported fractional stock | Product mutations/pull BLOCKED; standalone reference CRUD origin NOT_APPLICABLE in article-v1 | BLOCKED | BLOCKED | Each supported source → consumers BLOCKED; import may resolve references |
| C03 | Exact textual identity, rename preserves barcode/item | BLOCKED | BLOCKED | BLOCKED | Each source → other three: BLOCKED |
| C04 | Offline durable mutation, process reopen and automatic ACK | POS outbox → Admin BLOCKED; Admin offline-outbox origin NOT_APPLICABLE | BLOCKED | BLOCKED | Offline POS/Android/iOS → supported consumers BLOCKED; Admin mutations use online RPC |
| C05 | Same/different field conflict and pending local pull | BLOCKED | BLOCKED | BLOCKED | Authorized two-client pairs: BLOCKED |
| C06 | Product/category/supplier tombstone and reactivation | Product mutations/ref pull BLOCKED; standalone ref tombstone origin NOT_APPLICABLE in article-v1 | BLOCKED | BLOCKED | Each supported source → consumers BLOCKED; no mobile reactivation command inferred solely from deletedAt mapping |
| C07 | Import apply, correlated ACK, recipient pull and restart | BLOCKED | BLOCKED | BLOCKED | POS/Admin/Android/iOS import → supported consumers: BLOCKED |
| C08 | POS sale, return, void, lost response, one economic effect | BLOCKED_SCOPE | NOT_APPLICABLE ledger origin/reader; stock consumer BLOCKED | NOT_APPLICABLE ledger origin/reader; stock consumer BLOCKED | POS → Admin ledger and resulting stock → mobile are supported; articles/zero-sales readiness does not authorize this live run |
| C09 | New image fixture replace/remove, cache version and restart | BLOCKED | BLOCKED | BLOCKED | Each supported source → other consumers: BLOCKED |
| C10 | Historical prices and contract-specific History Entry | Prices BLOCKED; mobile History/session NOT_APPLICABLE | Prices/History BLOCKED | Prices/History BLOCKED | Price projections follow each contract; shared-sheet History/session is Android ↔ Admin ↔ iOS, with no POS consumer/origin |
| C11 | Session revoke/expiry, shop/operator change, delayed generation | BLOCKED | BLOCKED | BLOCKED | Each authenticated caller and destination: BLOCKED |
| C12 | TEST restore, pending preservation and reconciliation review | BLOCKED_SCOPE | BLOCKED_SCOPE | BLOCKED_SCOPE | Restore owner/window and exact client artifacts pending |

All `BLOCKED` cells require fresh readiness and installed authenticated artifacts
from the Mac coordinator. `NOT_APPLICABLE` above expresses source contract
boundaries, not runtime acceptance: Admin `553c456` sales-sync/pos-contract use
`pos-sales-ledger-v2`; Android HistoryEntry and SupabaseSessionBackupRemoteDataSource,
and iOS `8dfbf9` HistoryEntry/HistorySessionRemoteSupabaseAdapter use
`shared_sheet_sessions`. POS catalog payload domains are categories/suppliers/
products/prices. POS economic effects update the canonical inventory stock, so
their propagation to mobile remains a supported blocked comparison. Source
contract/local tests remain separate evidence. No live dataset size, latency
sample or remote effect was measured by this execution.

Mobile ~3s convergence has no current sample or PASS. POS retains its runtime
24–36s catalog cadence with 5s partial resume. No cross-clock subtraction or
invented performance threshold will be used. PR112 hosted evidence remains
historical; Asus Integrated/Final is not promoted from it.

Current standalone net48/x86 catalog measurements use the existing workflow
protocol:2 warmups plus20 samples,19763 synthetic products/prices, page1000.
All20 preserve exactness and expected bounded SQL counters (123 context,
20 relink,140 price commands/378 statements). p50=2460.45ms,
p95=2688.34ms,max=2734.49ms; dispatcher median=12.66ms,max=21.49ms;
peak working set60,166,144 bytes/private42,647,552 bytes,maxGen2=2.
Existing15s/96MiB/dispatcher gates pass. Delta10/100/1000 measures
46.02/45.06/103.02ms, one sample each; no delta quantile is inferred.
A bounded100000-row probe verifies all products/prices and stage cleanup,
21.21s,603 context commands/100 relink/700 price commands/1900 statements.
These are actual x86 fixture measurements, not the current live catalog size,
mobile convergence, ReleasePack-bound stability or monitor latency.

One current, untraced100000-product Diagnostic5 run completes all155 service
samples,31 render samples and five public scan samples. Its canonical receipt
is `MEASUREMENT_COMPLETED=true`, `ENVIRONMENT_VALID=false`,
`STABILITY_PASS=false`. Every environment sample reports `desktop_matches=0`
and `own_foreground=0` while the session/window are active/visible. These are
observed precondition failures; their system cause is not inferred. No desktop
or Windows protection was modified. The measurements are retained in
`D-cart-diagnostic/`; they cannot qualify Asus Integrated12 or Final60. Those
require an executor on the real interactive input desktop with the QA foreground
and the exact verified payload/harness/budget. Repeating a long soak with known
invalid environment would not establish acceptance.

After the user's desktop confirmation, an untraced short Diagnostic5 and one
existing execution-capture diagnostic reached the interactive desktop. The
first/only environment sample has desktop_matches=1, own_foreground=1, active
visible session, AC power and no interruption. Both runs then fail
`qualification_visual_scan_timeout` on scan1: quantity2 is applied, IsBusy is
false, but the existing250ms Input probe does not complete in time. The capture
observes a528.877ms Normal-priority WPF
`System.Windows.Documents.TextEditor.OnTextViewUpdatedWorker` callback spanning
that probe. This identifies measured blocking work, not its underlying driver,
IME, automation-client or application cause. No assertion, priority, timeout,
observer or accessibility feature was altered to produce a green result.
The incomplete result remains MEASUREMENT_COMPLETED=false/ENVIRONMENT_VALID=false/
STABILITY_PASS=false; one valid environment sample is not a qualification PASS.
One further short existing trace reproduces the same failure and observes the
same callback527.258ms, with AppDomain CPU109.375ms/allocation1,787,176 bytes
(includes worker activity). Its watchdog identifies that active callback;
observed observer-lock waits are0.007–0.010ms. This does not identify the inner
blocking operation. Evidence: `D-cart-diagnostic-interactive/`,
`D-cart-diagnostic-interactive-capture/`, `D-cart-diagnostic-interactive-trace/`
and `D-interactive-launch.json`.
The earlier desktop mismatch is therefore resolved by this launcher; current
Asus stability is open because of this observed visual probe failure.
Microsoft's [WPF reference-source archive](https://github.com/microsoft/referencesource/blob/main/wpf/src.zip)
defines TextStore/TSF layout/display-attribute and conditional IMM composition
updates inside that worker. These are candidate paths for UI-thread stack
sampling, not attributed causes or a symbol match to the installed servicing
binary. No product change is justified by the scalar trace alone.

The finite concurrent WPF load fixture now passes behavior/integrity on100000
synthetic products:40 committed pages ×500 rows, one validated/reopened snapshot,
24 public scans and24 rendered payment previews, zero sale/outbox/output changes.
Observed fixture worker peaks are1 per lane and the existing catalog run guard
is released. The snapshot contains one complete page generation and preserves
fractional stock and its remote shadow. All samples use one Stopwatch clock;
first calls are retained, nearest-rank quantiles. Scan p50/p95/max:
172.76/204.21/290.87ms; payment preview29.57/36.45/47.29ms; Input probe
3.84/8.71/13.01ms. Private bytes after setup/observed peak/after cleanup:
98,799,616/178,630,656/178,204,672. The peak uses50ms samples plus UI endpoints;
this short allocation growth is measured, not evidence of a leak-free long run.
The first run's busy-notification counter and net48 JSON formatting defects are
preserved in `load-first-failure.raw.json`/CSV and corrected without relaxing
assertions. No latency/memory threshold was invented for this fixture. Its
operation overlap is distinguished from the measured native-copy interval;
one observed worker does not certify the live scheduler or global single-flight.

## Current dispositions

| Status | Result |
| --- | --- |
| SOFTWARE | B/A/C_MERGED_AFTER_EXACT_HEAD_CI_SECURITY; D_DELIVERY_RECORDED_IN_POST_MERGE_RECEIPT |
| AUTHENTICATED_CROSS_PLATFORM | BLOCKED_FRESH_READINESS_AND_INSTALLED_ARTIFACT_RECEIPTS |
| PERFORMANCE | PARTIAL_LOCAL_CATALOG_GATES_PASS_AND_CONCURRENT_LOAD_MEASURED; ASUS_VISUAL_PROBE_FAIL; LIVE_AND_BOUND_STABILITY_OPEN |
| WIN7_HARDWARE | NOT_EXECUTED_TARGET_AND_PERIPHERALS_NOT_ESTABLISHED |
| RELEASE | FINAL_DOC_MAIN_DOWNLOAD_VERIFICATION_REQUIRED; RESULT_IN_POST_MERGE_RECEIPT |
| PRODUCTION | NOT_AUTHORIZED_NOT_ACTIVATED |

## Causal findings and completed features

| Finding | Before / cause | Correction and retained proof |
| --- | --- | --- |
| Product revision ordering | Two baseline failures: an older upsert resurrected a newer tombstone; an older tombstone removed newer reactivation. Page-start identity alone also allowed a later stale duplicate within the same page. | Known revision guards run before product/reference/image/shadow changes; accepted page revisions update the comparison state. Legacy rows without known revisions remain compatible. `A-product-revisions-before.trx`, `A-sync-revision-after.trx`, follow-up red/green receipts. |
| Fractional stock | Baseline mapper/detail reads turned 1.25 into 1. CSV ignored fractional input; transport/editor/cart stock DTOs were integers. | Decimal stock through existing SQLite affinity, DTOs, CSV/workbook, local/remote writes and transport. Local entry allows 0..Int32.MaxValue and three decimals; remote supported values are preserved without applying local rounding. Import transport uses the verified Admin 999999999/three-decimal domain. Invalid values are row errors. `A-fraction-before.trx`, `A-fraction-corrected.trx`, import/reader bound receipts. Sale quantities and CLP prices stay unchanged. |
| Omitted CSV stock | Rename with absent stock overwrote existing stock with zero. A proposed pre-transaction fetch could race another writer. | Preserve omitted stock inside the existing catalog mutation gate and transaction; explicit stock remains explicit. Deterministic concurrency regression preserves 3.456 after a competing committed change. `A-omitted-stock-summary.json` and red/green TRX. |
| Backup writer evidence | Old finite writer/polling could finish before the copy; a stronger test initially failed because no successful commit overlapped the backup operation. DELETE-mode native copy blocks commits while it runs. | Event-controlled first successful COMMIT while the backup operation is active, before native copy, then continuous bounded writer through verified completion. Counts follow COMMIT; no claim of a COMMIT inside `sqlite3_backup_step`. Initial failure retained in `D-core-first-failure.trx`; final three repeats × five cases all pass. |
| Portable printer profile | A valid physical automatic receipt plus manual virtual consent was incorrectly rejected by export policy. | Export preserves consent; import still disables automatic output. Actual virtual automatic printing remains guarded. Added regression included in the full suite. |
| Display privacy/lifetime | Portrait price fields ignored existing privacy flags; minimized cashier preview could return before starting its expiry timer. | Portrait uses the same flags as landscape. Monotonic expiry starts before opening and restores the prior projection; minimized five-second preview expires without activation or retained timer. |
| Logo runtime fallback | Actual WPF scenario failed on the deliberate pixel-bomb rejection. `InvalidDataException` does not inherit `IOException`; both the fixture rejection filter and managed-load fallback omitted it. | Add that explicit expected exception type, retaining unexpected-error propagation. Six matching-hash malformed/header-bomb managed fixtures cross the identity gate and demand safe fallback. Original failure retained; corrected `CUSTOMERDISPLAY_polish` passes. |
| Scanner public event | Actual existing WPF regression failed on public scan 1: H1 used only PreviewKeyDown while the public entry raised KeyDown. | Both events share one handler; handled preview cannot submit twice. Guard/IME/modifiers/assertions/timeouts stay unchanged. After correction, 50 public scans and 20 repeats pass before the separate focus-precondition failure. `H1-public-scan-first-failure.txt`, independent routing review. |

One review initially alleged that input 1.234 becomes 1234. Existing parsing did
not reproduce that allegation; it was withdrawn, and the fractional regression
was retained. Golden corpus expectations were not rewritten to hide a failure.
Rejected setup attempts (shop binding, copied test not discovered, stale
incremental DLL) remain recorded and are excluded from qualification counts.

H1 adds typed scanner Enter/Tab/both, bounded exact affixes/normalization,
isolated armed scanner test, receipt 58/32 and 80/42 profiles with compatible
aliases, drawer presets, atomic audited settings and selected-queue diagnostics.
A timed-out native diagnostics call retains the single-flight slot until it
actually finishes. Print/drawer services fence the current operator immediately
before output; synthetic inventory tests prove zero effects after an operator
switch. No physical output was sent in this execution.

H2 adds bounded strict schema1 portable profiles, checksum/diff/confirmation,
atomic import/reset/audit, service permissions and observed-version audit.
Allowlisted portable values exclude secrets/session/shop/trust/outbox, queue,
device, paths, RAW command, logo/free text and unknown keys. No secret digest
replaces redaction. Hardware resets fail closed, schedule defaults off, and
imports reuse the OPERATIONS-1 store/activation transaction and a valid existing
local destination. Runtime settings notifications reload language/hardware/display
without exposing partial generations.

H3 adds bounded PNG/JPEG/BMP managed logos, immutable basename/hash identity,
idle welcome/message/clock, barcode and money privacy, unsaved synthetic preview
and sale-free 5–60-second test pattern. Timers/subscriptions/disposal and operator
fences have Core/Data and real WPF fixture coverage. Win7 monitor/DPI/hot-plug
acceptance is still separate.

## Validation and limitations observed

Locked restore passed with pinned SDK10.0.301. Required gates pass 49/49;
dialog standards pass 35/35 plus printer extensions. Initial gate failure
referenced the old display-preparation signature and was corrected without
removing ordered preparation or bounded privacy checks. Initial build failures
were decimal propagation and an unobserved dispatcher operation; corrected
solution, WPF net48/x86 and harness builds have zero warnings/errors.

The first mixed candidate full suite ran 1200 tests:1199 pass/1 fail/0 skip
(backup overlap). Corrected focused package tests:153/153. Frozen Core/Data
suite:1201/1201, zero failures/skips, 3m32s. After the WPF/runtime fixes, the
final frozen candidate `508ba21` again passes1201/1201, zero failures/skips,
4m36s. Machine counts/hashes are in `D-local-validation.json`;
the original failed TRX is preserved, not overwritten by the green run.
Its exact-head canonical hosted CI also passes the complete WPF functional
runner, including all thirteen scenarios and the public focus assertion. This
hosted PASS does not change the observed invalid Asus interactive environment.
The subsequent source-identical C-main run37145182291 passes Core/Data and the
first twelve WPF scenarios, then fails the existing
`PERF_visual_lifetime_and_public_commands`: `Background did not progress`.
Its log and job metadata remain in `D-C-main-CI-first-failure.*`. No relation
to the Asus TextEditor callback is inferred from that message. This post-merge
FAIL is distinct from the exact PR-head PASS and requires final same-SHA green
checks; a documentation-only candidate is not itself a causal runtime fix.
The existing harness already writes phase/stack to `cart-regression-error.txt`,
but the runner did not print it on failure and hosted CI does not retain that
temporary directory. D now prints that existing synthetic diagnostic and
`harness-error.txt` before the unchanged failure throw. This fixes demonstrated
diagnostic loss, not dispatcher behavior; scenarios, budgets, assertions,
timeouts, loopback isolation and failed exit status remain unchanged.

Actual Asus x86 runtime passes product-image DPAPI/profile isolation and net48
serialization, authorization lease with two separate prepare/verify processes,
bounded asynchronous logging and responsive 100000-row product paging. Logging
observed queue high water256 and clean worker shutdown; 102817 intentionally
dropped messages under the bounded flood, no unbounded producer allocation.
These fixtures use new data roots and a loopback/non-routable backend, not the
authenticated staging vault.

CLI fast, backup/restore and failure selftests pass. Functional restore validates
the business fingerprint with pre-backup and app services; fault suite reports
18 restore points,9 cancellation points,3 crash recovery points,10 backup fault
points and1 startup recovery point with zero residue and valid old/new outcomes.
Disk FULL/IO/readonly failures are injected. Real SMB and live TEST restore are
not inferred from them. OPERATIONS-1 remains disabled by default, preserves
minimum three verified managed copies, UNC fail-closed, durable watermark,
activation/audit and recovery; the in-process scheduler cannot run at POS closed.

The canonical visual run produces50 screenshots over five resolutions and
login/operator/POS/payment/refund/product/import/settings/receipt/recovery
surfaces. New hardware, display and fractional-editor scenarios pass, as do
stable batch identity, recovery/restart, discount preview, integrated local
sale/retry/receipt/restart and editor late-render close. The final existing
performance/lifetime scenario progressed through public scans/repeats, images,
recycling and lifecycle but failed its focus assertion with `hostActive=false`.
One unchanged isolated recheck failed the same active-host precondition. The
unchanged product focus guard deliberately declines focus for an inactive
window. No source defect or identity of the foreground window is inferred, and
the focus assertion/timeout has not been weakened. This local environment
result is retained separately from canonical hosted qualification.

Independent read-only reviews: hardware reviewer for B and A; backup reviewer
for H2; operations reviewer for H1/H3 and both runtime corrections. Each receipt
binds the reviewed commit/diff or source hashes and records independence; no
GitHub human approval is invented. All reported source findings are resolved.

## Integration receipts

| Package | Qualified source / exact PR head | PR / normal merge | CI/Security |
| --- | --- | --- | --- |
| B | `7158e3a92351ff76809ef60f024417f206e97007` | [114](https://github.com/XNIW/Win7POS/pull/114), merge `319a488a3d4c166e586bc8b3460c733725d6443d` | Exact-head CI37139978300 and Security37139978258 pass; relevant build/SBOM/reproducibility/CodeQL all green |
| A | source `441bd0cbdf6473c6cadc03612ad1815625f0638b`; main integration head `a6449f54ad904944ca9278e00f2c756a20848006` has identical source | [115](https://github.com/XNIW/Win7POS/pull/115), merge `b3a19ffc80b659e445bd49f351184b3a0a245120` | Exact-head CI37141454901 and Security37141454922 pass |
| C | Distinct H2 `7d75a25`, H1 `1695538`, H3 `9ca46dc`, logo fix `d7111f1`, scanner routing `d4a7790`, load fixture `508ba21`; integrates current main | [116](https://github.com/XNIW/Win7POS/pull/116), exact head `508ba21e112d4e0c3097fdde89bb569ef967948a`; merge `8a25bd217362b3143ff1045b110b3fc9c9c6f7fc` | Frozen local full suite1201/1201 and builds pass; exact-head CI37143736148 and Security37143736107 pass, including canonical WPF scenarios |
| D | This report, current docs, consolidated receipts and failure-only diagnostic printing in the existing runner; no src/tests/workflow changes | Normal PR on `codex/closeout-evidence-release-20261003`; identity/merge in post-merge receipt | Exact-head CI/Security precede merge; final exact-main CI/Security/pack and download verification recorded below |

The package-A worktree was created because C had active writers; it integrates
the B merge without switching their checkout. Both owned baseline and package-A
worktrees were archived successfully with recoverable snapshots; unrelated worktrees
and protected image checkpoints remain untouched.

External rows stay open with a specific owner/prerequisite/action; no new owner
deferral is inferred.

## Final-main delivery receipt

The report cannot contain the SHA of its own eventual merge commit. The single
post-merge machine receipt is
`C:\Dev\_codex-evidence\win7pos-closeout-20261003\final-main-delivery.json`.
It binds this versioned report to the actual D PR/head/merge, final local and
remote main SHAs, clean/ahead-behind status, unchanged src/tests/workflow trees
relative to the qualified C merge, the one reviewed runner-diagnostic delta,
and successful CI/Security/ReleasePack on that same final
SHA. It records run attempt/version, verified archive digests, Setup/payload ZIP
and manifest hashes, file count/architecture/provenance/attestation, signature
stage, and the path/hash of `download-verification.json`. A workflow PASS alone
does not satisfy delivery; the canonical downloaded-artifact verifier must pass.

The delivery command is
`pwsh -NoProfile -File scripts/win7pos/windows/test-downloaded-release-pack.ps1 -RunId $FinalReleasePackRunId -ExpectedCommitSha $FinalMainSha -OutputDirectory $NewOutputDirectory`.
GitHub artifact ZIP digests are distinct from payload/Setup/per-file checksums.
Only `development-unsigned` delivery is authorized here; installation and
physical/signing acceptance remain unexecuted unless separately attested.
The final user response links this report and that machine receipt, without
creating another narrative report or another commit solely to embed its own SHA.

The same receipt records the authorized Mac handoff, any returned checkout or
readiness receipt, and the actual Asus main update. It must not claim that the
Mac IDE, installed mobile callers or staging database were updated by Windows.
The local WPF runner restores its process-local loopback settings in `finally`;
synthetic databases and private evidence are retained. No real data, credential
vault, remote fixture or protected historical image checkpoint is deleted.
TEST processes are inspected by their owned harness identity; absence of a
remaining process is recorded only after observation.
Versioned console-log copies remove end-of-line display padding so the ordinary
Git whitespace check passes; originals remain in private evidence. The machine
receipt `D-log-display-padding.json` records before/after hashes. Test results,
TRX and assertions were not edited. `D-evidence-inventory.json` distinguishes
captured-file hashes from Git's text-normalized blob identities.

## Concrete residuals and next execution

These rows are open acceptance for this candidate. Historical owner-accepted
backlog dispositions remain intact; this execution creates no new deferral or
PASS for those builds.

| Residual / impact | Owner/channel | Required prerequisite | Single next executable action |
| --- | --- | --- | --- |
| Authenticated C01–C07/C09–C11 and reopen/delta coherence; no record-level four-platform proof yet | Mac coordinator, chat **Riprendi collaudo ecosistema POS**; W backend and N mobile writers | Fresh maintainer common/POS readiness (<2h), deployed backend/database receipts, installed authenticated client artifacts and exact final POS ReleasePack; TEST vault already validates | Coordinator supplies the readiness run ID and window; Asus runs `pwsh -NoProfile -File scripts/qa/Invoke-Win7PosStagingAcceptance.ps1 -Profile asus-staging -ReadinessRunId $ReadinessRunId -ReleasePackRunId $FinalReleasePackRunId` without bypass. Its two-process article receipt closes only the scenarios actually covered; coordinator completes the remaining shared fixture directions. |
| C08 economics and C12 restore/reconciliation; article readiness is zero-sales | Mac coordinator with backend/restore owner and QA operator | Explicit compatible synthetic sales/restore window, scope authorization, recoverable TEST data, epochs/outbox/pre-backup and exact installed clients | Coordinator issues a separate sales/restore readiness receipt and runs the existing app procedures against its synthetic fixture, retaining one-effect economic/stock comparisons and reconciliation review. |
| Asus bound stability; Interactive/Limited launch resolves desktop mismatch but scan1 visual probe times out | Win7POS QA/source owner with Asus interactive operator | Reproduce and identify the long TextEditor callback without changing250ms probe/observer; exact verified payload/harness/budget and active QA window | Use the existing short Diagnostic input-dispatch comparison to isolate public-input scheduling from the observed WPF callback; fix only a demonstrated cause, then run ReleasePack-bound Integrated12 and Final60 with the frozen budget. No reduced duration, desktop/protection or observer/accessibility bypass. |
| Windows7 SP1 app/installer lifecycle, scanner and physical 58/80 print/drawer/display/DPI/hot-plug; no current physical certification | Project owner and identified hardware QA operator | Available real/VM Win7 SP1/net48 x86 target; identified authorized peripherals and test data; final pack | Install the verified exact candidate and execute the existing Win7/hardware backlog matrix, recording OS/device/artifact and observed paper/drawer/display results. A VM result remains distinct from physical peripherals. |
| Real SMB backup/restore; simulated UNC/disk faults do not attest a share | QA operator responsible for the TEST share | Identified writable authorized TEST SMB target using Windows-configured access; synthetic app data | Configure that TEST destination through the app, run one verified backup/restore/restart cycle and retain integrity/business/audit/cleanup receipts. |
| Mac IDE checkout update | Authorized Mac coordinator | Preserved current edits/checkpoints and final Win7POS revision available | Coordinator fetches/integrates safely and returns old/new SHA, preservation and clean/ahead-behind receipt for the checkout actually updated. |
| Real release signing/timestamp and production certification | Release/project owner under a future explicit production mandate | Existing protected-release policy, authorized real certificate and RFC3161 service; all required production/hardware acceptance | Use the existing Protected Release procedure only under that mandate. This closeout delivers development-unsigned; no purchase, signing protection change or production activation occurs. |

## Residual execution — 2026-10-04

`WIN7POS_RESIDUAL_FIRST_SCAN_AND_LIVE_SYNC_RESULT` resumes initial clean
`main=origin/main=80a6cd82b6c8538df7846190e757409bebe721dc`, Release Pack
37148719320, on `codex/residual-first-scan-20261004`. The original delivery
receipt is preserved as
`C:\Dev\_codex-evidence\win7pos-closeout-20261003\residual-20261004\initial-final-main-delivery.json`.
The current receipt remains `C:\Dev\_codex-evidence\win7pos-closeout-20261003\final-main-delivery.json`;
the single current evidence index is `residual-20261004\evidence-index.json` in
that private evidence root. Post-merge PR/head/main, CI/Security, package and
qualification identities are recorded there rather than by another documentation
commit chasing its own SHA. Backup, product revision/fractional stock, H1/H2/H3
and OPERATIONS-1 were not reimplemented. The finding “1.234 diventa 1234” remains
withdrawn.

The previous applicable Integrated12 has 640 scans and passes; Final60 retry4
fails scan1 at595.377ms with a valid actual-child environment sample. Its
TextEditor callback spans about522ms in the TextStore/layout/automation/native
wait path. These immutable results remain historical. They do not apply to the
changed driver, observer metadata and protocol5.

### First-scan experiment and qualified scope of the correction

The initial short, release-bound reproduction still fails scan1. A redundant
SelectAll hypothesis was then tested with one product variable changed, five
fresh processes per arm in alternating AB/BA order and the same synthetic cart.
Baseline completes3/5; selection guard completes1/5. All environment samples
are valid. The hypothesis is refuted; the product experiment was reverted.

The next comparison freezes one complete diagnostic build and changes only
command delivery. The direct driver inherits DispatcherSynchronizationContext
priority **Send**, as measured by the trace; the original tentative “Normal”
description is corrected here. In five fresh processes per arm, alternating
AB/BA with controlled reintroduction, direct delivery fails4/5 first probes
(observed580–593ms); queued Input delivery completes5/5, all25 scans. All
environment samples remain valid. The first complete counted UI durations in
the Input arm are152.269–201.557ms, including enqueue, intervening callbacks,
service, apply, probe and layout. The first probes are66.031–90.352ms. One direct
arm also passes; no unsuccessful arm or first sample is removed.

The scheduling effect is therefore demonstrated for the synthetic QA driver.
The correction queues the same public command at Input. Microsoft's
[DispatcherPriority documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority?view=windowsdesktop-10.0)
documents this input queue priority. This does not identify the internal cause
of the TextStore/provider wait, establish a product bug or attribute it to an
external automation client. The trace identifies measured blocking work only.
No product source changes, automation/accessibility/IME suppression, WCT retry,
native injection or system configuration changes are included.

An additional equally instrumented comparison exercises the real TextBox
binding and shared PreviewKeyDown/KeyDown public handlers at Input: three fresh
processes and15 scans per arm, command and routed input, all pass. This is
synthetic WPF routed input; native scanner delivery, hardware and native IME
remain unmeasured. It does not replace the full input/focus regressions.

Protocol5 starts the existing stopwatch **before** enqueue. The complete
command overhead formula and250ms Input probe are unchanged; bitmap time stays
separate. A controlled dispatcher callback regression verifies its actual
elapsed time is included. The250ms limit is the post-busy probe limit, while
the unchanged whole-UI maximum is1000ms; these are distinct gates. The validator
now checks visual wait and whole-UI maximum for **every** sample, including
first/warmup samples previously exempted. Warm percentile conventions and the
frozen budget are unchanged. Red/green validator logs preserve both defects.
`DiagnosticInputDispatch` supports frozen protocol4 diagnostics only; it is
explicitly a no-op for protocol5 and cannot form another v5 A/B comparison.

### Actual-child environment, cleanup and regression evidence

The actual UI child now samples the canonical desktop/session/foreground/
visibility/power/interrupt conditions after fixture readiness, before the first
scan and useful clock. It flushes the decisive sample and stops immediately
once cumulative validity is lost. Runtime evidence records process/session/UI
thread, working directory, desktop names and the actual backend environment's
scheme/server without credentials, path or query. Parent readiness alone cannot
qualify the child. The interactive wrapper uses the logged-on user with
Interactive/Limited, repo working directory and the real Admin backend override
`http://127.0.0.1:9`; its legacy override is also kept identical. Wrapper changes
are separately hashed in the index.

Supervision owns only its returned Process object. Terminal cleanup tries
graceful close, bounded wait and owned kill fallback; exceptions cannot skip
fallback or unconditional process-local environment restoration. Regression
fixtures cover failed supervision, close exception, incomplete terminal state,
failed cleanup receipt publication and nonzero child exit. They use fake owned
processes; they do not claim a real elapsed production timeout was induced.
The UI regression also proves hidden-host invalidity remains terminal and its
sample is flushed before disposal.

The targeted public-input/lifetime regression passes after the user manually
reactivates the desktop. Earlier failures, including the measured Screen-saver
desktop and a test-file sharing defect corrected with shared read access, are
retained. It covers real binding, Enter/Tab, shared handled preview/bubbling
single submission, KeyDown-only, composition guard, focus recovery/inactive
window guard, Unicode editing, selection, Rows/Grid and image recycling. The
isolated scanner fixture is restored in finally. No native IME or physical
accessibility certification is inferred.

The subsequent first untraced protocol5 short process stops **before scan1**:
desktop_matches/session_active/visible=1, own_foreground=0, child terminal=true.
It is an environmental nonqualification, not a latency failure or first-scan
PASS. No identical automatic retry or long soak follows that lost prerequisite.
Current short, Integrated12 and Final60 outcomes, including any later changed
prerequisite, are bound individually in the current machine receipt. Neither
traced comparisons nor the old Integrated12 qualify the final protocol5 payload.

Independent read-only input and QA reviews find no blocking defects; their
reviewed file hashes and final small accounting/metadata/compatibility fixes
are retained in `review-receipts.json`. Required architecture/dialog checks and
49/49 gates, solution/WPF builds with zero warnings and1201/1201 Core tests
with no skips pass. The full local WPF suite produces50 screenshots and12
passing scenarios, then reproduces “Background did not progress” at the image
editor's ancestor-visibility resume. Its early environment sample is valid;
no sample attests the environment31s later at that failure. One bounded,
phase-local diagnostic replay passes without reproducing the delay, so its
cause remains unknown. The immutable diagnostic build/trace is retained and
that temporary instrumentation is removed from the patch. This is not a fix
or a discarded failure. The final untraced targeted regression also passes
with terminal exit0; it does not retrospectively repair the full-suite FAIL.
Targeted rechecks and exact-head hosted checks are
recorded in the current receipt with their actual terminal results.
The historic CI “Background did not progress” uses a separate3000ms Background
regression probe. No link with the first-scan Input250ms timeout is established.

After the later actual-child foreground-valid observation, the final source
candidate completes all five predeclared untraced fresh-process runs,25 scans,
with every environment sample valid. First probes are105.213/113.011/114.120/
62.940/78.081ms; complete counted first UI times are340.742/197.889/353.885/
147.199/147.406ms. The full UI maximum and the probe limit are both respected;
the first complete UI time is not universally below250ms. These source-built
Diagnostic runs remain explicitly unqualified and do not substitute for
exact-main package binding, Integrated12 or Final60.

### Live owner handoff and remaining execution gates

The authorized former Mac coordinator **Riprendi collaudo ecosistema POS**
redirects this execution to **Completa integrazione e collaudi**, chat
`01a10506-e42c-7352-a6cb-08b7bc8c800b`. The request covers backend deployment/DB
compatibility, installed **authenticated** Android/iOS callers, native
recovery→reopen→subsequent delta, common/POS readiness and preserved Win7POS
Mac checkout before/after. Existing mobile/IDE and backend/Admin writers remain
the sole owners. Source CI, saved/installed builds or UI readability alone do
not prove those gates. The earlier locked-Mac observation is superseded by
supported UI readability at17:06Z; input/auth/session readiness remains a
separate requirement. Owner receipts and later updates are linked in the index.

The Mac owner's terminal checkout receipt attests before/after
`81acd479c187469fe0dc31f9b0fb3a162312c1cc` with13 dirty files unchanged;
an update to this execution's final main is NOT_RUN. Backend deployment is
reported at22107a6f, distinct from Admin82af13ef with deployment SKIPPED.
POS contract compatibility and terminal authenticated caller are not attested.
New mobile/backend fixes make old source/artifact snapshots historical. The
vault profile again passes binding/DPAPI/ACL checks without credential export.
No live request, remote SQL, deployment or remote fixture mutation has been
performed by Asus without canonical owner readiness. The existing C01–C12
matrix and justified NOT_APPLICABLE directions above remain intact; articles/
zero-sales readiness cannot authorize C08, C11 scope changes or C12 restore.

| Gate | Concrete next action and proof |
| --- | --- |
| FIRST_SCAN / ASUS_STABILITY | Source short5 is complete with25 scans and valid actual-child environment. Verify its applicability to the frozen final binding, repeating short proof only for changed material bindings, then execute new exact-main ReleasePack-bound Integrated12 and uninterrupted Final60 with unchanged budget and no concurrent QA/build load. Preserve every terminal result. |
| POS_ADMIN_LIVE | Backend owner provides canonical fresh POS article readiness run ID/scope/window and deployed-contract receipt; Asus invokes the existing vault-based acceptance runner against the exact final pack and new authorized data directory. |
| MOBILE_CROSS_PLATFORM | Coordinator finishes installed authenticated caller receipts and record-level reopen/second-delta comparisons for supported C01–C12 directions; economic/session/restore actions require their specific scope. |
| MAC_CHECKOUT | Mac owner safely integrates final Win7POS main while preserving13 dirty files and returns actual before/after SHA and checkout receipt. |
| WIN7_HARDWARE / SMB | Targets remain explicitly unavailable. Existing exact-package install/upgrade, scanner Enter/Tab, offline/reconnect, sale/return/void, print recovery, backup/restore/restart checklist is ready; Asus loopback/Epson does not certify Win7/Xprinter/SMB. |
| RELEASE / PRODUCTION | Ordinary reviewed integration, exact-SHA CI/Security and canonical downloaded-pack checksum/signature/provenance verification precede delivery. Development-unsigned only; production remains unauthorized. |
