# WIN7POS post-PR122 recovery completion

## Baseline and evidence

The integrated baseline is Win7POS `8ede85ee37ed9ecd0af83203df8551cc5a91f6aa`
(PR122), with Admin `62f513f6ca15662e6bbb384a7510977fe68e1267` (PR132).
The PR122 installer remains a historical baseline. Final source, workflow,
package and served-runtime identities are recorded from observed results in
the delivery receipt, rather than by a later documentation commit.

The private evidence directory is
`C:\Dev\_codex-evidence\win7pos-post-pr122-20261009`.
Initial failures, intermediate failures and subsequent results occupy separate
directories. WPF remains net48/x86; Core and Data remain netstandard2.0.

The initial interoperability reproduction used the C# builder, SQLite outbox,
real client serializer and Admin's official loader/parser/handler. The saved
ordinary original omitted `attemptCount`; all six U2 requests were rejected
with HTTP 400 before authentication/RPC. A mock HTTP 200 was not used to claim
contract acceptance. A separate baseline WPF reproduction confirmed that edits
were lost after closing the recovery window, without changing applied data.

PR122's 5000-row result established virtualized display and rejection of a
1105961-byte request. Its supported final local commit test covered 1000 rows.
It did not certify completed 5000-row Admin reconciliation.

## Implementation

Ordinary import and recovery use durable operation groups attached to the
existing outbox. Each ordinary request is limited independently by row count
and the complete serialized UTF-8 HTTP body. Planning includes the maximum
allowed escaped credentials and transport metadata; sending measures again
with current credentials. Saved operation identities and payloads remain
stable on retry. Local product, stock, history, membership and outbox writes
share the existing transaction and follow a verified backup.

Uncertain large originals use bounded proof uploads of the complete immutable
saved JSON. The server verifies uploaded bytes, operation identity and its own
canonical hash. Receipt lookup remains a snapshot; replacement requires an
authoritative retirement fence. Large recovery plans bind complete row coverage
to saved children or accepted contributor receipts. Partial ACKs preserve
progress and keep the original unresolved.

Additive migration 0014 stores group membership, operator drafts, shared
correction proof, the prepared-plan journal and immutable succession archives. Migration 0013 and previously
integrated checksum material remain unchanged. A correction group shares one
original/receipt proof locally, avoiding one full copy per child.

The journal preserves the prepared children and exact remote plan document
before its first upload. Remote registration and local economic commit are
separate states. A draft can be discarded without deleting a possibly sent
plan. Changing an already sent plan requires authoritative reconciliation and
succession; clearing a local journal is not a recovery mechanism.

Draft edits survive close/reopen and are never automatically applied or treated
as remote confirmation. Reopening checks original/hash, shop/session, generation,
permissions and revisions. The dialog exposes draft status and confirmed
row/part progress in four languages, using the existing dialog resources,
ownership rules and virtualized grid.

## Verification scopes

| Evidence | Result and scope |
| --- | --- |
| Original C# U2 bytes against Admin `bee0670` | 7 parser and 6 handler checks pass; authentication dependency is synthetic. Omitted/zero original attempts are accepted, while ordinary sends still require a positive attempt. |
| Complete HTTP reader boundary | 524287/524288 bytes accepted, 524289 rejected before RPC. |
| Two recorded PostgreSQL 5000-row ordinary response archives | Reingestion through the C# sync service passes: 5000 product IDs, 10000 price IDs, saved values and closure only after all five ACKs. Economic SQL ran in the owned isolated Admin database; this is not a live staging result. |
| Uncertain-original multipart corpus | Exact C# bytes in corpus `7e287719d52d854908fd532be86ae45af46aefcf` were replayed through official Admin handlers and economic SQL in the owned isolated database. The real response archive published at Admin `7faecb54` is pinned by SHA256 `f813a28b28a6eabf10d651b4ac569d5a4fd1dc78e7a42086b3eb2226461afbbf`. C# reingestion passes 9/9 cases, verifying all 5000 product IDs, 10000 price IDs, stock, edited values and history. Outer authentication is synthetic; this is not staging or physical Win7 qualification. |
| Lost third response | Restart retries only the third operation with identical bytes after the existing 30-second backoff. The same official Admin receipt is reingested; the local root remains unresolved until its fifth durable ACK, even when the recorded server parent is already complete. Stock/history are not reapplied. |
| Shared correction planning | 5000/60000-row local planning tests pass, with one shared proof. This does not qualify the upper limit on Admin, WPF or physical Win7. |
| Legacy upper local reconciliation | 60000 product IDs and 120000 price IDs reconcile against the full SQLite history in 20 seconds, preserving stock and original bytes. A preceding run was stopped after demonstrating repeated full-history scans; an equivalent existence query removes that cause. ACKs in this upper test are synthetic: large Admin proof normalization remains a separate qualification. |
| Succession wire generation and real reingestion | Corpus `9a87550244a00a322d70bc066ac19c0a84f6c494` preserves 115 unchanged HTTP requests across four 1001-row sequences: mixed accepted/retired parts, lost retirement response, empty-child successor and lost empty-child registration response. Admin `3da549a5` responses initially failed C# reingestion because `plan.parts[].itemCount` was omitted, before the first child retirement. The corrected actual response archive at `2a34851c` has SHA256 `7baef9303b7be9c20ae4dc183aae0e305c4948efea035a7ab42dc3521d876797`. All four positive roundtrips and four historical negative cases pass, verifying every request byte/hash/route/index, lost replies, 1001 product IDs, 2002 price IDs, stock/history, deferred drafts and partial-to-complete group progress. Original request JSON is unchanged. Economic SQL is real and isolated; outer authentication is synthetic. An intermediate enum-label comparison failure in the test harness is kept distinct from the original server defect. |
| Phased upper input provenance | Checkpoint `51964ac2ec242a6f0f75c0ee65d44ad57a99a352` contains real builder/SQLite/precommit planner inputs for 5000 and 59999 dense Unicode rows. The latter represents 60000 worksheet rows including its header: original 58466463 bytes, recovery plan 71664104 bytes and 120 children. SQLite integrity and raw hashes are verified. This is planning evidence; the real Admin cursor schedule and byte-exact replay remain separate qualifications. |
| Dense 5000-row Admin cursor probe | The first probe stopped at coverage projection with `projection_too_large`; no economic writes had occurred. Admin `26f06a84988d1e56d18ed3db7237430dcc976990` removes a duplicate nested item projection without changing the persisted proof or HTTP limits. Resuming from the failed cursor completed ten real ACKs, 5000 products and 10000 prices with stock 1.25. C# schedule-driven wire capture and fresh equivalent-database replay remain separate qualifications. |
| Phased client boundaries | 81 focused tests, 33 upload/progress tests, six schedule-selector tests and four 128/129-count routing tests pass. A prior trailing-JSON validation failure is preserved separately. Request sizing, fresh authorization, cancellation, stable hashes and bounded manifest paging are covered; these tests do not replace the real Admin database replay. |
| Invalid Unicode before writing | Seven focused tests pass: NUL and unpaired surrogates are rejected with a row/field error before recovery backup or business writes; valid paired emoji and CJK survive serialization. |
| Deferred draft after an accepted contributor | Two current-service tests pass through startup before and after the new ACK. The previous status, receipt and aggregate proof remain immutable; the new correction keeps the shop barrier until its own ACK and uses distinct historical price IDs. Stock remains 1.25 and duplicate ACK is harmless. Initial startup schema rejection, runtime `legacy_contract_mismatch` and overwritten historical ACK are preserved separately. The startup fix changes runtime validation only, preserving historical migration SQL and checksums. |
| Original retirement reply lost | Initial client/SQLite qualification passed seven of nine tests and exposed two restore defects: small correction target/receipt missing locally, and multipart transport options missing after an explicit authoritative retirement retry. The same unchanged test source now passes nine of nine, including six identity/hash/shop refusals. Ordinary local fallback already passed. Lookup `conflict/identity_retired` still provides no fence and cannot authorize commit. These tests use explicitly synthetic peers; real handler/database retry evidence remains separately qualified. |
| Admin draft candidate automatic gates | PR134 head `54a7a3cf7c01e60ea032d9d7444e9b445fa36511`: CI `38012792208` Verify and migrations/pgTAP pass; Cloudflare build `38012792107` passes. Staging E2E and deployments are skipped. The earlier `ce1493e7` source gates remain separately recorded. This source is a draft candidate, not the final integrated or served runtime. |
| Migration and dialog structural gates | Additive ledger/checksum guard and 36/36 dialog checks pass on the current source. Final canonical gates bind the frozen candidate. |

Local WPF and harness x86 compilation after all four review fixes succeeds
with zero errors and warnings. Frozen-candidate canonical qualification remains
separate. Final
local `IMPORT_RECOVERY_completion` launch was rejected by automatic approval
review with `blocked by policy`, with no more specific reason. No equivalent
local retry is used. Final UI qualification uses the canonical independent
Windows CI runner after source freeze. Earlier intermediate UI failures remain
in the evidence and are not presented as final passes.

The prior PERF failure occurred in its own process. U2 windows in a different
process do not establish its cause. Existing comparison with a plain WPF control
does not establish absolute release of native window roots; the owner of the
observed retention remains unidentified. No timeout or budget was increased.

The development review and new restart tests identified additional succession
defects: locally pending retired parts could still drain before final closure;
an unapplied successor could lose the last applied stock baseline; inherited
accepted parts could lack local ACK publication; unchanged successor rows could
reuse a retired identity. These findings, initial failures and fixes are kept
separately. Succession, deferred contributor drafts and empty-child convergence
passed their earlier focused restart tests. A later current-service test
identified incomplete aggregate provenance when an accepted contributor covers
part of a member and its recovery covers the remainder. The fix records both
authoritative sources and passes the focused tests. Further review reproduced
an edited descendant value being rejected against the ancestor's frozen value,
then a subset descendant proof being accepted. Six focused tests now pass with
complete row coverage and distinct historical price mappings. Their initial
failures remain separate. Applying a deferred draft to an already accepted
contributor also reproduced a startup schema rejection before its new ACK;
the runtime and immutable-receipt fixes now pass both complete restart tests
and the independent review. These findings do not establish
missing remote economic effects from receipts already reconciled.
Locked restore and all 49 canonical source gates pass on the complete source
checkpoint `2717037` with the corrected interop tests. They remain separate
from final exact-SHA hosted qualification and package verification.

## Subsequent source qualification

The immutable 249-file checkpoint at `7ae17532b7d1f7e609cf5db471689e7c918409a7`
passes locked restore, 49/49 gates, solution build, 1584/1584 Core/Data tests
with no skips, CLI selftest, and WPF/harness x86 compilation with no warnings
or errors. The local test run took 12 minutes 10 seconds. Its TRX SHA256 is
`450451c47b22fcd4f8412093cf36a2df23db0d7595f0f5fb31cbec4b408c2d76`.
Security run `38014290431` passes on that SHA. Hosted CI `38014290304` was
cancelled when the branch advanced; it did not complete Core/Data or WPF
qualification and is not reported as a hosted pass.

Checkpoint `cd89ba8472c1f02d643e1ac027af183cb995d915` adds only the dense
test-helper selection and strict price-kind comparison, plus its immutable
capture fixture. Its first official Core.Tests capture passes 1/1 without skips
in 10.273 seconds. All 169 C# HTTP bodies follow Prepare, repeated original
proof, explicit retirement, commit and ten individual ACKs. The 169 responses
are unchanged actual Admin pilot replies; 41 additional QA frames remain
explicitly accounted for. Every body is at most 350059 UTF-8 bytes. All 5000
product IDs, 10000 price IDs, stock 1.25, 10000 historical rows, original bytes
and final queue closure are checked. The ZIP SHA256 is
`1c8633eeb70db8d6b0ec8b5fadf9c42ab20a5f9fcad8606a7e5f35791b53b90e`.
The helper's prior case mismatch was a static assertion defect, not a product
or SQL failure. Independent review closes it and verifies the complete
169/41 partition and all archived byte hashes. Fresh-database replay and C#
reingestion of its newly recorded responses remain separate steps.

Admin source `2bcee6e84c5a6a8cdb5c8165bf235848b981152d` preserves the collision
checks while projecting scalar identities before the global join. The SQL
LF SHA256 is `e88d17783f89fc307bb2f62c1d2e5d6fb645fa6d3c3b3a4b7d723b50d94f9aca`.
CI `38015030219` and Cloudflare build `38015030261` pass. The isolated staging
candidate `acb3e4ac0eb41568ce9980dc82f1b293a6fd2e35` independently passes CI
`38015414663` and build `38015414649`; deployments and staging E2E are skipped.

The upper executor records the original timeout at cursor 59750 before any
economic writes, followed by an identical-request retry in a new equivalent
clone with unchanged timeouts. Its composed continuation completes 120 durable
ACKs, 59999 products, 119998 price mappings and stock 1.25. The prefix uses
the earlier SQL source and the continuation uses `e88`; this is not a uniform
source or uninterrupted maximum-volume qualification. PostgreSQL VmHWM was
sampled at about 2.15 GiB; the complete peak and Worker memory were not measured.
The failure database remains preserved.

The new Admin replay of all 169 unchanged C# bodies completes in a fresh
isolated database, with ten durable ACKs. Its runner initially confused
child receipt `complete` with group `parentStatus`; the first successful apply
response was preserved and only the remaining requests were dispatched after
correcting that assertion. Each request was dispatched once. Publication of
the resulting proof archive was rejected by automatic approval review under
the Admin AGENTS.md constraint. A minimal response-only archive was subsequently
acquired through read-only command output in the authorized conversation:
175 segments, 1069478 bytes, SHA256
`3351fd42d40c5b07385f3aa61cc00dc84f513a615494ee7dd9e4da6c617880c5`.
Every segment, ZIP member and raw reply hash was checked mechanically on Asus.
No alternative remote-repository mutation was attempted.

The first official C# reingestion passes 1/1 without skips in 9.019 seconds;
the permanent test passes 1/1 in 9.039 seconds with a normal build and no
transient runner. It regenerates all 169 service requests and compares their
exact SHA256 before consuming each actual fresh-database reply. Prepare,
retirement, local commit and ten individual sync ACKs complete. All 5000
product IDs and 10000 price IDs match; stock, history, immutable original and
closure are checked. The test and response fixture are mandatory, with no
mock or missing-evidence fallback. This happy path uses actual parser/handler
and economic SQL, with synthetic dependency schema and outer authentication;
it does not qualify a served Worker, physical Win7, or a new dense crash run.

Hosted CI `38016143128` on `cd89ba8472c1f02d643e1ac027af183cb995d915`
passes 1584/1584 Core/Data tests, locked restore, 49 gates, CLI selftest,
x86 WPF/harness builds, product-image serialization smoke, functional visual
checks and IMPORT_completion. IMPORT_RECOVERY_completion then reaches its
unchanged 60-second timeout. Later functional scenarios and runtime checks
are not qualified by that run. The FAIL and full log remain preserved.
The timeout cause is not proven: that branch did not expose its last subcase
or wait phase. A concrete race in the harness's premature confirmation click
is addressed by waiting for the actual owned ContentRendered dialog within
the existing ten-second bound. Added phase diagnostics survive timeout; the
canonical wrapper's scenarios, timeout and assertions remain unchanged.
The existing manual WPF workflow can execute that same wrapper and retain
diagnostics, without replacing required canonical CI qualification.

Hosted manual workflow `38019175087` on `e73dfe3c88e60ef2dfd61e5e9b326cc6cc720206`
passes all 19 functional scenarios and 50 visual captures. Its 11 recovery
subcases and 106 waits complete. Canonical CI `38019158101` on the same SHA
passes 1585/1585 Core/Data tests, including the mandatory actual dense 5000-row
roundtrip in 27.385 seconds, but its recovery scenario again reaches the
unchanged 60-second bound. The phase trace reaches the 5000-row local commit;
the 1000-row subcase has not started. No deadlock is established. The largest
extra interval against the manual run is between language cases, outside the
recovery operation waits. Both complete observations are preserved separately.

The QA fixture initialized the full schema fifteen times, and phase reporting
rewrote its complete growing evidence file on the UI thread. The harness now
initializes and validates one empty schema inside the same bounded scenario,
then makes independent new database copies with matching byte hashes, full
migration-ledger checks, integrity/foreign-key checks and empty economic,
outbox, plan and draft state. Each fixture still runs the real generation,
import and local blocking paths. Evidence is appended in full order without
discarding records. Setup, capture and language-switch phases expose remaining
costs. All eleven subcases, assertions, waits and memory/timeout budgets remain;
no application source or process boundary changes. This removes redundant QA
work; its timing benefit must be observed in the next exact-SHA hosted run.

The first pristine-copy hosted run, `38021741032` at `b5f33c6`, fails in
fixture setup before any recovery action: `File.OpenRead` cannot hash the
source because a disposed Microsoft.Data.Sqlite connection remains pooled.
The preserved stack points to `HashFile`, not a recovery transaction or
timeout. The harness releases only the private source connection pool before
disposal and publishes the reusable path/hash only after validation succeeds.
Target fixture pools, file sharing, integrity checks, assertions and budgets
are unchanged. This QA correction still requires exact-SHA hosted execution.

## Operational qualification

The isolated Admin staging candidate must contain the final protocol changes
and be qualified on its exact SHA. Shared TEST DDL, staging Worker deployment,
the existing QA secret update and smoke remain subject to one updated concrete
change proposal. Build and merge alone do not prove the served runtime.

READY requires the authoritative complete current contributor registry,
classification of shared bootstrap, current metrics and fresh deployment
readback in the consumer's exact 22-field format. The old observed disjoint image
runs do not establish a clean QA universe. Live acceptance starts only after
valid READY and a final Release Pack, with a fresh verified download directory.

Android's active source is MerchandiseControlSplitView. Mobile propagation,
physical Win7 installation/peripherals/backup and read-only historical business
audit are reported only when actually executed with the corresponding access,
operator, recoverable TEST data or authorized business copy.

## Targeted plan reuse after the b312 canonical timeout

Canonical CI `38022219497` on `b31200bae6ee499242c7646bf14a2bb777e41b54`
passes 1585/1585 Core/Data tests with zero skips, including the actual dense5000
request/reply regression in 37.484 seconds. Visual, import and ten recovery
subcases pass, including the local 5000-row commit. The unchanged 60-second
recovery process then expires during the nested dialog rendering of its final
1000-row case. This remains a canonical FAIL; the earlier hosted manual PASS
does not replace it. The phase trace is a prefix of the completed manual trace
and does not establish a deadlock or an application lifetime fault.

The trace and source expose redundant plan construction. Recovery CommitAsync
previously built a complete plan during its fresh preview, discarded it, then
built the same plan again after checking for a durable prepared plan. It now
keeps the fresh preview and plan together within that single invocation. A
durable prepared plan still takes precedence. Public preview validation,
synchronous guards, the cancellation checkpoint, permissions, current local
baselines, backup and transaction remain in their original order; no plan is
cached between calls or reused after a subsequent operator edit.

The WPF QA comparator also built an extra complete recovery plan solely to
assert unchanged rows and applier query shape after the UI had already verified
the recovery. For the explicitly guarded standalone never-sent fixtures, it
now uses the applier preview of the same immutable row snapshot. It preserves
the unchanged-row and dry-run assertions; UI validation and commit still run
their complete recovery service paths. New phase markers measure this
comparator. Screenshots, all eleven subcases, process boundaries and timeout
and memory budgets are unchanged.

Two new behavioral cases (3 and 1001 rows) pass against both the previous and
updated service. They check that preview has no economic effects, a later
operator edit is captured freshly, and committed operation identities,
payload bytes and hashes match the plan for the new intent. Targeted recovery,
prepared-journal failures and zero-child convergence tests pass 57/57. The
permanent actual dense5000 corpus passes 1/1 and the 115-request actual Admin
succession/negative corpus passes 8/8 with the updated service. WPF/harness x86
build has zero warnings/errors; required gates pass 49/49. The WPF runtime and
canonical results of this delta remain pending until an exact-SHA hosted run.
Local WPF actions previously denied by automatic approval review are not
repeated.

## Isolated recovery setup after the 82de72a canonical timeout

Canonical CI `38025955514` on `82de72a6a95004180395ea2cdd0094cb221e6d6f`
passes 1587/1587 Core/Data tests with zero skips and all 49 required gates.
The mandatory actual dense5000 request/reply case passes in 26.162 seconds.
The WPF recovery process nevertheless reaches its unchanged 60-second limit.
All 421 recorded phases match the completed manual run in order and all 106
recorded waits complete, including the final 1000-row commit wait. Its subsequent
backup, progress and replacement checks are not completed, so this remains a
canonical FAIL. The separate hosted run `38025970075` passes 19/19 scenarios,
11/11 recovery subcases and 50 visual captures; it does not replace the failure.

The isolated recovery scenario initializes a default database in its parent
runner and then initializes its own private pristine schema. Source review
confirms that this scenario uses explicit fixture factories throughout: the
trusted store uses JSON/DPAPI, generation construction is pure, and Sync Center
passes the fixture factory to status, recovery and nested dialogs. The parent
now omits only its unused default initialization for the exact isolated
`IMPORT_RECOVERY_completion` scenario, with performance mode excluded. Aggregate
and all other scenarios retain the existing initialization. The full private
schema initialization, integrity/foreign-key/ledger/empty-state checks, 15
independent byte-exact copies, fixture imports, all eleven subcases and all
assertions, screenshots, process boundaries and budgets remain unchanged.
The previous pre-first-phase interval also includes startup and trust work;
its whole duration is not attributed to the omitted initialization. A new
exact-SHA canonical execution must qualify this reduction.

The canonical workflow now uploads only functional diagnostic text, CSV and
PNG files after success or failure. It discovers the existing temporary root
without changing TEMP/TMP or moving the workload to another drive. This makes
the final phase, observations and captures reviewable after a failed runner.

Separately, a guarded owned testhost is forcibly terminated after reading the
actual Admin reply for an accepted dense5000 part, before returning any HTTP
response byte or storing its local ACK. Same-generation startup preserves the
in-progress claim. Thirty-one read-only observations wait for the unmodified
900000 ms production lease; the same-body retry occurs after its actual expiry.
The final proof compares 171 client attempts, 170 actual archived Admin replies,
17 SQLite snapshots, all 5000 product IDs and 10000 price IDs, unchanged stock
and history, and parent completion only after the tenth ACK. The middle test
run is intentionally aborted; the initial and resumed runs pass. Server replay
and client reingest are separate phases with synthetic outer authentication;
this is neither a deployed Worker test nor a crash inside an open SQLite
transaction. The evidence bundle retains the initial QA fixture failures and
the frozen source identity; it is not substituted for canonical qualification.
