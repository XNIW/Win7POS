# ASUS-W7POS-019 execution ledger

Mandate: explicit Execution A+B+C request of 2026-11-04. Normative R3 input SHA-256
`47b9de5c4ced21f6deaa139717b153d8ea0d57ada761dfd3607f6bd193bfcf28`.
Baseline local HEAD, origin/main and GitHub main:
`32618249fc01f29e86c6774d2b2c9262a90f226f`; branch main. Preexisting plan,
index and worklog edits preserved. No reset, stash or clean.

| Requirement/gate | Status | Candidate / command / evidence | Dependency / next step |
| --- | --- | --- | --- |
| Repo sync | PASS read-only | GitHub GET main and local Git; same baseline, eight recent commits recorded in PLAN | Recheck before delivery |
| Toolchain | PASS SDK / PowerShell | SDK 10.0.301 from pinned local installation; PowerShell 7.6.9 | No dependency upgrade |
| Original input availability/integrity | PASS independent audit | Read-only ZIP/OOXML private manifest outside checkout; all expected controls matched; source hashes unchanged | Not a product parser PASS |
| A parser / pure rules | FOCUSED_PASS | Product preview on original corpus also PASS on run02 net48/x86 binaries; independent controls and source hashes preserved | Final candidate recheck after currency/time audit correction; real apply still gated |
| B persistence / authority / recovery | FOCUSED_PASS | Five real foundation scenarios PASS in focused run015030831; prior failures retained | Exact-final code SHA/CI qualification pending |
| C reports / UI / export | FOCUSED_PASS | Latest focused Smoke8/8,44/44 assertions; actual Load11/11 | Full Windows run03 remains FAIL; exact-final CI pending |
| QA automation/public privacy | FOCUSED_PASS / FINAL_CI_PENDING | Closed receipts validated, current focused jobs cleanup=true/remaining0; run03 public publication PASS separate | No overall Windows PASS inferred from focused evidence |
| Real time semantics | BLOCKED_EXTERNAL | Modern timestamp markers alone do not prove provider semantics | Authoritative provider/voucher evidence |
| Real shop/overlap | BLOCKED_EXTERNAL | No authoritative real-shop mapping/start date supplied | Binding, permissions and overlap proof |
| Physical Windows 7 | NOT_RUN | Build host cannot substitute physical qualification | Candidate package and owner card |
| Main/CI/security/package | NOT_RUN | No implementation commit yet | Complete functional candidate first |

Build/restore/test writer: root agent only. Delegated component file ownership is
nonoverlapping. All raw diagnostics and real financial controls stay private.
Previous FAIL/TIMEOUT evidence must remain available; no result is inferred from
static inspection, missing prerequisite, or a receipt schema alone.

Current resume point: run03 ended with unchanged source and retained FAIL;
Core1692/1692 and gates50/50 PASS. Focused Recovery and foundation5/5 subsequently
PASS. Latest focused run021708997 completes Smoke8/8 (44/44 assertions) and actual
Load11/11 on unchanged fingerprint, with cleanup=true/remaining0. The latest audit
maintenance regression plus native proof tests pass23/23. Code SHA remains pending;
these results are not a full Windows rerun or a relabeling of run03. Root remains
the sole build/test writer.
Earlier process/next-command notes below are historical checkpoints; this pointer
and the newest run section are authoritative.

## Intermediate execution evidence (2026-11-04)

- Data Release build: PASS, zero warnings/errors on the early integration candidate.
- WPF Release net48 x86 build: PASS on the intermediate candidate; six warnings in
  report code were reported to its owner. Later source edits are not qualified by it.
- Dialog standard: PASS, 37/37; no positioning/sizing exceptions introduced.
- `historical-ledger-initial.trx`: 0/8 PASS, schema postcondition rejected added
  native-table triggers. Corrected with narrow native writer/proof hooks; published
  migration materials/checksums and detector meanings retained.
- `historical-focused-03.trx`: 29/35 PASS; six failures were unregistered backup AUX
  contexts while their implementation was pending. No authority bypass added.
- `historical-ledger-04.trx`: 6/8 PASS; deferred undo-operation FK corrected before
  the next run. Migration0015 is unpublished working code; prior pins unchanged.
- `historical-backend-05.trx`: 125/126 PASS; one WAL-source backup regression found.
  Corrected owned backup partial normalization before read-only validation; source
  stays read-only. Exact final regression remains to be run.
- Full Core/Data run06 is RUNNING with the pinned SDK, one test writer. Canonical
  existing Core budget is 3600s (historical baseline measured 35m56s), not the generic
  600s default. No timeout was increased. Raw/TRX evidence is retained externally
  under the task-private test directory. Source has advanced after this test build;
  its result will be attributed to that intermediate binary candidate only.
- New foundation/smoke/load workers and closed receipt validators are implemented
  but NOT_RUN. CI remains CrashProbe until C is qualified; no synthetic PASS emitted.
- Operator confirmed the new Shop/account has not yet been created. This blocks
  real binding/apply qualification, not implementation or synthetic gates. Timestamp
  values/offsets in exports alone still do not resolve provider time semantics.

Current root-owned process: the single dotnet Core/Data run06 and its testhost;
no WPF or POS operational process launched. Do not delete its data/evidence.
Next: finish static integration review; collect run06 without rerun; build final
harness; run focused new regressions and canonical QA gates serially. Run paths,
source identity and process cleanup must be recorded before delivery.

## Budget clarification

The R3/user mandate states 60000ms per process for all twenty functional cases.
Inspection found the baseline wrapper had a 120000ms exception for FUNCTIONAL_VISUAL.
Execution applies the explicit mandate: all twenty now use 60000ms. No timeout is
raised or adjusted after a failing run; any resulting failure requires diagnosis.
The previous baseline discrepancy is preserved here rather than silently described
as unchanged. The historical foundation/smoke/load budgets remain 360/90/240s.

## Integrated working candidate before canonical Windows qualification

- Full Core06 completed: 1595 PASS / 53 FAIL / 1648 total, 12m38s, zero skipped.
  FAIL evidence retained. Root causes were explicit owner bootstrap, additive
  migration expectations/ledgerless recognition, a UI/Data boundary violation,
  and a duplicated native authorization demand; each was corrected and covered.
- Regression07: 298 PASS / 3 FAIL / 301 total. The three negative fixtures queried
  an absent ledger after the new earlier fail-closed check; their postconditions
  now accept no ledger or zero ledger rows while retaining unsafe-schema rejection.
- Native commit08: 11/11 PASS, zero skipped, including the unchanged 10s final
  safety budget and stock/outbox rollback behavior.
- Integration09: 185/185 PASS, zero skipped, covering import corrections, receipts,
  source integrity, schema negatives, report query/CSV/time policy and architecture.
- Fingerprint10: 22/22 PASS, zero skipped, including literal ASCII/Unicode digests,
  source handle protection, replay after revision/restart and merchant bounds.
- Latest WPF/harness Release net48 x86 build: PASS, zero warnings/errors.
- QA AST and Runtime tests: PASS; synthetic child output sentinels preserved.
  Owned process identity/deadline structural tests and typed TRX/gate projection
  tests PASS. These are infrastructure checks, not historical scenario PASS.
- Source privacy scan: no original corpus names/hashes/financial controls found
  in repository source, tests, documentation, scripts or workflows.
- Host verified: Windows 11 Home Single Language 10.0.26300 x64; SDK10.0.301,
  PowerShell7.6.9, Node24.18.0. This does not qualify physical Windows7.
- GitHub main rechecked at the baseline SHA; branch unprotected and effective rule
  list empty. No push has been attempted; no branch/PR created.

All component owners have frozen their files. Next command: canonical Windows
suite with HistoricalSalesMode All, sequential dependencies foundation -> smoke
-> load. C consumers are implemented; this is their integrated qualification,
not a claim of prior runtime completion. CI remains CrashProbe until C passes.
Do not edit repository files during the run: the orchestrator checks before/after
source fingerprints. Preserve all failures and private diagnostics.

## Canonical Windows run 01 — retained FAIL

Run ID: `Win7POS.QA.20261104T233753242Z.ce1ebda28b0c4253b6365a88abf2310a`.
Private evidence resides under the task-private qa-runs directory. Original reports,
all failed gate transcripts, child receipts and partial historical receipt retained.
No historical scenario PASS was inferred: foundation executed0/completed=false;
load NOT_RUN after dependency failure.

- PASS: restore, WPF build, harness build, authorization lease, bounded logging,
  paging/Dispatcher. Functional:14 PASS/6 FAIL. Overall FAIL; Core/selftest NOT_RUN
  because solution build failed in the linked CatalogPerformance fixture.
- Source gates found stale migration0014 sequence and inline-only commit markers.
  Corrected append-only0015 checks and deep helper/route validation; all four
  targeted gates PASS, including23 negative transaction/route mutations.
- CatalogPerformance now links the same owned fixture helper and its explicit
  test-only friend access. Solution `Win7POS.slnx` now builds with zero warnings/errors.
  An earlier manual `.sln` typo failed before build and was corrected to the actual file.
- Native proof v2 includes void markers; standalone void transaction ownership is
  in the transaction writer. NativeProof12:23/23 PASS, zero skipped.
- Historical WinExe workers were not awaited and an unrelated default-slot marker
  obstructed restart. Fixed with the existing waited helper/residual deadline and
  actual per-fixture authority before initialization, not a marker bypass.
- Secondary synthetic DBs and backup readers in five functional helpers/ProductImage
  now have explicit lifetime ownership or exact read-only snapshot contexts.
- Clipboard failure was a verification-read busy HRESULT after a single copy action.
  Verification yields only for that observed condition within a fixed1s subdeadline;
  it never repeats copy or increases the60000ms scenario budget. Content checks remain.
- JSON timestamp coercion rejected safe partial evidence. A strict JsonDocument
  reader now preserves date strings/Int64 on supported PowerShell hosts, rejects
  duplicate keys and bounds input. Original partial receipt structure was revalidated,
  without changing its incomplete execution status. PowerShell7.4 itself was not run.
- Latest QA AST/Runtime, process identity structural and projection tests PASS.
  Public finalization now preserves earliest primary and separates publication status.

Next: second canonical Windows/All run on the frozen corrected source, followed by
necessary focused diagnosis and exact-final CI/security/Release Pack delivery.
Do not edit source during a canonical run. PhysicalWin7, real account/binding and
provider time evidence remain distinct external gates.

## Canonical Windows run 02 — retained FAIL

Run ID: `Win7POS.QA.20261105T000608931Z.6138891a87cb4bc6a19a7d49b5146c99`.
Before/after source fingerprint is identical:
`4c489cb8831285ff4f1d1f793e8cfb429a1bd5350dc424ff858e8cce583ac490`.
Overall exit1; first failure is the source gates. Public evidence publication PASS
is separate from scenario qualification. Every launched root reports cleanup=true
and zero remaining processes. All raw failures and incomplete receipts retained.

- Solution, WPF and harness builds PASS. Core:1682 PASS/1 FAIL/1683 total, zero
  skipped; the backup settings fixture exceeds the Win7 destination length bound
  under owned TEMP. Fix fixture names, not the production limit.
- Selftest, authorization lease, bounded logging and paging/Dispatcher PASS.
- Functional:18 PASS/2 FAIL; all twenty retain the60000ms budget. ProductImage
  also FAIL; diagnose exact synthetic path/authority failures before rerunning.
- Historical foundation: genuine HOT recovery PASS; subsequent supervision FAIL.
  No complete foundation PASS. Historical smoke:seven scenarios PASS, lifecycle
  assertions pass but disposal fails, so scenario8 remains FAIL. Load NOT_RUN.
- Restore and receipt source gates still use old inline markers after the AUX
  and shared preview refactors. Replace them with deep checks and negative
  mutations, retaining their safety assertions.
- Independent review found a custom-role report scope defect: DailyCloseView
  without RegisterViewAll could request all operators. Repair the real loaders,
  immutable filter-bound publication permit, and epoch-only UI invalidation.
  Add actual VM regressions before the next canonical qualification.

The original seven private inputs were also read by the actual product reader
on net48/x86 using the run02 Core/Data binaries. Private probe PASS: independent
input controls matched, hashes unchanged, NeedsEvidence=true and CanApply=false.
The probe is read-only (no DB/apply/UI), outside the checkout; its financial
controls and input identities remain private. Windows11 net48 qualification does
not replace P-REAL apply/reimport/undo or physical Windows7 qualification.

Run02 is complete; source thawed for the corrections above. Root remains the
single build/test writer. Next: focused regressions, then frozen canonical
Windows/All on the corrected candidate. CI remains CrashProbe until C qualifies.

## Focused corrections after run02

- Data13:41/41 PASS, zero skipped, on an owned TEMP root matching the canonical
  path length. Covers rejected foreign/missing currency, explicit temporal choice
  in fingerprint/audit, receipt/replay/undo and the shortened backup fixture.
- Authority15:7/7 PASS, including distinction of lock contention from path/media
  failures. A later additional Win32 HRESULT facility assertion awaits final CI.
- Restore gate14:FAIL retained. Its new negative fixtures exposed an extractor
  that only recognized Task-returning methods, making five mutations inert.
  Fixed declaration extraction/comment handling and require actual mutation.
  Gate16 PASS, including all12 unsafe mutations; no production assert removed.
- Receipt gate16 PASS, including six unsafe analytical/print mutations. Process
  identity16 PASS for synthetic collector/departure and structural deadline
  contracts; this does not attest native process sampling or the B real probes.
- Actual WPF Release net48/x86 build after scope/epoch changes:PASS, zero warnings
  and errors. Harness runtime qualification still pending.
- ProductImage fixture basename shortened; authority failures now distinguish
  sharing/lock violations from path/access/other IO. Recovery pristine hashing
  closes the initializer's distinct pooled writer before exclusive byte checks.
  Functional scanner fixture waits for actual public CanExecute within its
  existing per-scan budget. No scan count, expected output or timeout changed.
- Before the first Load run, independent inspection identified incomplete
  measurement protocol bookkeeping: missing first/last/phase bounds and separate
  memory grid, thread-pool sampler and hardcoded completion. Complete the planned
  HISTORY-MEASUREMENT-v2 contract and negative truncation/gap/stall fixtures;
  do not certify a partial sample sequence or raise thresholds.

## Final integration corrections before run03

- Measurement17 FAIL retained: a PowerShell local `$expected` overwrote the
  `$Expected` context parameter (case-insensitive names). Renamed the slot count;
  Measurement18:82 pure protocol checks PASS, including truncation, missing
  phase/memory slots, shared pending probes, cost reconciliation, privacy and
  unqualified target-profile negatives. No actual Load PASS inferred.
- AutonomousRuntime19 PASS, including new cumulative B subbudget regressions,
  strict Load fixtures and bounded process/output handling. Reference limits
  remain30s analysis/15s DB apply/500ms warm; Win7's approved limits require an
  explicitly qualified trusted context plus hardware manifest identity.
- Intermediate final harness Release net48/x86 build PASS, zero warnings/errors;
  later sampler/read-authority corrections still require the next build.
- ReceiptMemory20 PASS:the previous six analytical/print mutations plus five
  mutations against passive authority/expiry/capture checks are rejected. R3
  forbids authorization/owner monitors on Dispatcher reads: immutable volatile
  authorization stamps replace those read-side latch calls. Final CSV permits
  continue to use the actual authorization latch outside UI callbacks.
- Finite review strengthened B subbudgets, distinct physical short-path proof,
  exact post-swap injected rollback proof, real keyboard/analysis heartbeat, and
  nonempty cart/command restoration after actual admission cancellation.
- A restore-to-export race was found in the cached report coordinator: an old
  token could remain memory-current after a completed restore/rebind. Bind it
  to current database lifetime authority; reject stale ObserveRead and serialize
  authority transitions with publication. Qualification remains pending until
  the dedicated regression and frozen canonical run complete.
- Reference host inventory was captured before the first Load:Intel Core Ultra7
  255H,16 logical processors,15.36GiB physical memory,NVMe storage,Windows11.
  Private inventory SHA256:
  `6281ecb90232147fb22ea327990b6758f8d1c373a4984d320d482901a61e448c`.
  This identifies the reference build host, not a physical Win7 target.

- AuthorityLifetime21:39/39 PASS, zero skipped, including four new lifetime,
  epoch, real restore and publication serialization tests. A nullable fixture
  warning was removed by explicitly rejecting a missing marker. Subsequent
  transition timeout semantics: failure to acquire the publication gate changes
  no stamp; successful transitions alone publish under that gate.
- Final harness build:PASS, zero warnings/errors. Its queued modal keyboard
  script is observed through its completion source; explicit discard of the
  DispatcherOperation removes the incidental compiler warning.
- RequiredGates22:50/50 PASS, including Dialog Standard and the QA protocol/
  negative suites. This is a preflight, not the full Windows qualification.

Next frozen command:

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode All -OutputRoot C:\Dev\_codex-private\ASUS-W7POS-019\qa-runs -PublicOutputRoot C:\Dev\_codex-public\ASUS-W7POS-019
```

Keep run03 failures/partial receipts if any. Do not edit tracked docs, source,
fixtures or workflows during the run; the before/after fingerprint covers them.
CI remains CrashProbe until the integrated C/load qualification is observed.

## Canonical Windows run03 — retained FAIL and focused continuation

Run ID:`Win7POS.QA.20261105T010900248Z.ea3306d57ec841c3bd06c6da4bcdc838`.
Source fingerprint before/after:
`3f9a943f47bfde406d1336253223f8399c06829c4e27340bb419cc9040920fac`.
Exit1; earliest failure functional-completion. Public publication PASS is a
separate result. Every launched step has cleanup=true and processRemaining=0.

- Core1692/1692 PASS, zero skipped,13m27s. Restore, gates50/50, solution/WPF/
  harness builds, CLI selftest, ProductImage, authorization lease, bounded
  logging and paging/Dispatcher PASS.
- Functional19/20:Recovery FAIL after its5000/1000 supported batches completed;
  fixture teardown attempted owner disposal while actual UI refresh was still
  running. Close/drain the real operation/refresh flags before fixture disposal,
  within the existing case60s budget. No database authority bypass.
- Foundation4/5:HOT, commit authority, admission/nonempty cart and backup/restore
  PASS. Entrypoint's case/shortpath and reparse/hardlink aggregate checks FAIL.
  Old evidence lacks individual variant codes:do not infer a production alias
  defect. Instrument exact native results/identities/codes privately and create
  a genuine owned NTFS junction; retain strict aggregate assertion IDs.
- Smoke6/8:H07/H08 failed at fixture creation with trusted_time_continuity_lost,
  after the harness exhausted the production limit of16 synthetic clock domains.
  Reuse one coherent monotonic synthetic domain and fresh response anchors.
  Product clock limits/high-water protections and all assertions remain unchanged.
  Load NOT_RUN by dependency; no performance result promoted.
- NativeProofNegatives23:22/22 PASS, zero skipped. Real schema0015/canonical
  outbox verification covers N1/N2/N3/NX, no current-shop fallback, all read
  surfaces and native writer/token invalidation. No Verified projection forged.
  These are additional tests after run03, not1692 tests relabelled as1714.
- Read-only privacy inventory:211 eligible changed/untracked files and10 OOXML
  parts, no private corpus identities/controls or operational artifacts. Three
  PEM headers are preexisting synthetic redaction vectors. Original hashes and
  metadata reconfirmed7/7 unchanged.

To avoid repeating already valid Core work for fixture-only diagnosis, reuse the
actual canonical Invoke-QaProcess supervisor/Job/isolated directories and existing
functional Scenario / historical Mode wrappers for focused regressions. Generate
fresh run/context/nonce from current source and binary hashes, validate real
receipts and cleanup, and preserve every failure. No alternate process launcher,
new mode, fabricated dependency status or overall Windows PASS is introduced.
After C/load qualifies, delivery still requires canonical CI All on the exact
commit, Security Supply Chain, Release Pack and separate QA payload.

## Focused continuation — retained failures and software qualification

The root reused `Invoke-QaProcess`, its existing Job supervision and the actual
functional `-Scenario IMPORT_RECOVERY_completion` / historical `-Mode` wrappers.
All contexts/nonces, owned directories and binary/source identities were fresh;
no alternate launcher, weakened assertion or fabricated dependency PASS was used.
These are focused canonical steps, not an overall Windows qualification.

| Focused run suffix (all `Win7POS.QA.20261105T`) | Observed outcome retained |
| --- | --- |
| `014153898Z.12f596f503db4ac98315474f16fc484e` | Recovery PASS,32.232s wrapper, cleanup=true/remaining0. Initial historical calls failed before scenario receipts because root omitted output-directory creation; setup evidence retained, not a product failure or PASS. |
| `014440523Z.681d379b6ab64dfc95b68b2e148d231e` | Foundation FAIL on shortpath distinctness; Smoke7/8, H08 failed before completing its assertion set. Load NOT_RUN. |
| `015030831Z.b1eb4ac536a844e38725d1c9779705ba` | Foundation5/5 PASS,25.264s receipt/26.389s wrapper, cleanup=true/remaining0. Smoke7/8, stale_result_ignored FAIL; Load NOT_RUN. |
| `015519428Z.ec48fc99c1eb48c1a88f30e7fe3d3b28` | Smoke7/8, stale_result_ignored FAIL; Load NOT_RUN. |
| `021148797Z.f9a515dc0e774933bb3ea192afe29b49` | Smoke7 PASS/1 NOT_RUN with incomplete H08; Load NOT_RUN. Preserve the blocked-reader diagnosis and process evidence. |
| `021708997Z.fc392d44a84e4e9888e683e0040dbe1d` | Smoke8/8,44/44 assertions; actual Load1/1,11/11 assertions. Both wrappers exit0, qualified=true, cleanup=true and processRemaining=0. |

Recovery now closes and drains the real Sync Center operation/refresh before
fixture disposal. Foundation variant evidence separately proves the case alias,
distinct8.3 spelling of the same physical file, hardlink and real NTFS junction,
with exact authority refusal codes. On .NET Framework48 `Path.GetFullPath`
expands8.3 names; compare the absolute raw Win32 spelling before that expansion,
while still requiring handle identity and rejection of the actual alias. No
production database authority change was needed for that test defect.

Fixture clocks share a coherent monotonic domain with fresh authoritative response
anchors; the product's bounded domain/high-water protections remain unchanged.
H08's stale-result proof now uses fresh valid sessions and genuine pending report
loads behind actual factory maintenance, across epoch invalidation and logout.
Both observed witnesses report pending=true, immediate=true, noOrphan=true,
cleared=true and unchanged=true. The epoch witness has zero SessionChanged events
at the immediate read; the logout witness observes its legitimate event. The
test never requires legitimate later logout events to remain absent.

The real held-maintenance probe exposed a production Dispatcher deadlock:
`SecurityRepository.LogEventAsync` synchronously called `_factory.Open()`.
It now awaits `_factory.OpenAsync().ConfigureAwait(false)`. The actual regression
`SecurityAuditMaintenanceTests.AuditReturnsAnAwaitableWhileMaintenanceIsHeld_ThenPersistsAfterRelease`
and `NativeSalesOwnershipProofTests` pass23/23, zero skipped, in private
`tests/security-audit-maintenance-25.trx`. This verifies awaitable audit admission
and eventual persistence after release; it is not an arbitrary tamper detector
or a deterministic CAS-race test, and is not a new full Core count.

Latest focused source fingerprint before/after is identical:
`f38745b6b1f1c6134daf9eecfb7081af977b8477fee3448a98f40f9832870e76`.
Harness SHA256:
`4416b901428eae14ffd0d12b8fef5195445ff8cea629f477270214dcacba3511`.
The source SHA in the receipt is still the baseline commit plus dirty working
tree; no final implementation commit SHA has been assigned here. Evidence is in
the private focused run directory named above; raw previous failures remain.

Load uses `HISTORY-MEASUREMENT-v2`, targetProfile=`reference-windows`, a dedicated
sampler thread and the fixed50ms Dispatcher/100ms memory grids. Its independent
flags are measurementCompleted=true, environmentValid=true and thresholdsPassed=true.

| Actual synthetic measurement | Observed value |
| --- | --- |
| Source / accepted / rejected | 50000 /49000 /1000 rows |
| Mixed report population | 10000 native +49000 historical rows |
| Analysis | 1390.464ms |
| DB apply / warm query | 7862.516ms /169.733ms |
| Dispatcher p95 / maximum | 0.464ms /16.341ms |
| Dispatcher probes / memory samples | 393/393 /197/197 |
| Pending / dropped / memoryDropped / overflow / suspended | 0 /0 /0 /0 /0 |
| Sampled peak private bytes | 86753280 bytes,82.734MiB |
| Smoke / Load receipt duration | 25.658s /21.812s |
| Smoke / Load supervised wrapper duration | 27.731s /24.088s |

The reference host and inventory identity above apply. No PowerShell7.4 runtime,
physical Windows7, scanner/printer or real corpus apply qualification is inferred.
The focused envelope explicitly sets fullWindowsQualification=false. Run03 is
still overall FAIL, including its historical Load NOT_RUN and original failures.

Final required-gates26:50/50 PASS, exit0, in private
`tests/required-gates-final-26.log`. CI now selects `HistoricalSalesMode All`
following observed C/load qualification. Final privacy inventory:216 eligible
files plus10 OOXML parts, clean; the external qualification card names the
applicable skill portably without a user-profile path. Final code SHA, CI All,
Security Supply Chain, Release Pack and exact separate QA payload remain pending.
