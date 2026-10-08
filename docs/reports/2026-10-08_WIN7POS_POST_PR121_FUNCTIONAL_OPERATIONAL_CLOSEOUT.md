# WIN7POS_POST_PR121_FUNCTIONAL_OPERATIONAL_CLOSEOUT

## Scope and provenance

Baseline: Win7POS main `bda04d86ea8dfb047697d74d793415efabc02820` (PR121).
Implementation branch: `codex/post-pr121-functional-operational-20261008`.
The final integration, exact-main workflow runs and new installer will be recorded
once, after verification, in the private final receipt under
`C:\Dev\_codex-evidence\win7pos-post-pr121-20261008`.
The PR121 package remains historical and does not contain these changes.

WPF remains net48/x86, with Windows 7 SP1 as the target; Core/Data remain
netstandard2.0. No framework or dependency upgrade, destructive queue reset,
historical business repair or direct POS-to-Supabase access was introduced.
R1 Int64/Excel, R2 AutoOpen and the PR120/121 safeguards remain in scope of the
existing regression checks.

## U1 — complete price input validation

The real WPF/SQLite baseline reproduced six mixed invalid/valid price cases:
the valid field was saved while the invalid field was ignored, both drafts
were cleared and success was displayed. A retail-only high local Int64 case
was separately preserved as a positive baseline.

The editor now distinguishes intentional blank, valid and invalid input and
validates both fields before writes. Nonblank invalid input blocks the whole
operation, displays a localized field error and retains both drafts and useful
focus. Blank preserves the current price; zero is valid. Both blank or unchanged
values do not create history, outbox entries or false success. Busy state starts
before asynchronous waits, preventing double confirmation. Service failure
rolls back product/history/outbox and retains an editable draft for retry.

Manual entry keeps its existing Int32 limit and CLP grouping policy. Local
retail Int64 storage and Admin's changed-price limit `999999999` remain distinct.
The global money parser was not broadened. Thirteen WPF groups cover both field
directions, limits, malformed separators, correction/retry, a real SQLite fault,
single-flight input and all four languages at 1024x768 logical size.

## U2 — durable recovery of blocked imports

The causal baseline used an import already committed locally: three rows, one
retail `2147483648`, two valid rows. Ordinary corrected reimport queued only one
delta and omitted the two locally unchanged rows, leaving the old import
unresolved. A second real HTTP loopback case received all three rows and deliberately
lost the response, simulating an accepted remote outcome without an Admin database
commit. A further baseline exposed an ordinary reimport receipt whose
unchanged purchase price had no matching new local history entry.

Sync Center now presents the cause, affected rows and an authorized recovery
action. Data, network, authentication and conflict causes have distinct operator
messages; technical IDs/codes remain bounded diagnostics. The recovery dialog
retains edits across validation errors and lookup/retirement. Cancellation rolls
back unfinished writes and closes after active work ends; drafts are not persisted
after closing the dialog.

Additive SQLite migration 0013 stores delivery, replacement and contribution
evidence. Legacy attempt counts are not treated as proof of delivery. An import
proved never sent can republish every still-owed original row. Uncertain delivery
requires an authoritative Admin receipt: `not_found` is a snapshot only. Explicit
authenticated retirement fences the old identity before replacement, or returns
the accepted receipt if apply won the race. Different payloads receive new
operation identities. Original JSON/hash/shop/correlation remain immutable.

Accepted imports use a linked correction limited to changed price/quantity
fields, original item/product IDs, a six-fraction revision and masked base
snapshot. Remote metadata is preserved. No-effect prices require an explicit
server receipt; current snapshot equality alone does not invent history IDs.
Blocked corrections can be retired/replaced through an ascending durable chain
limited to 128. A child already accepted is reconciled from its immutable intent;
a later unsent draft stays visible and is never reported as saved remotely.

Only edited local fields are applied. Original null/omitted fields stay absent
in the outgoing intent rather than taking preview fallbacks. Quantity edits
apply the difference from the already committed intent to current local stock,
preserving later movements; retry uses the child baseline to avoid reapplying
that difference. Ordinary remote quantity remains absolute, while accepted
correction uses a delta relative to the accepted original.

A verified backup precedes local economic writes. Payload hash, generation,
shop/epoch, receipt/contributor set and current authorization are rechecked in
the transaction, including immediately before commit. ACK mapping and ancestor
completion are atomic. Only complete authoritative outcomes release the
original and shop guard; other batches remain processable. Intermediate price
history can receive a leaf price ID only when its immutable value actually
matches the leaf's accepted intent.

The complete captured HTTP envelope is checked against 512 KiB before backup or
writes, and actual fresh credentials are checked again before dispatch. Correction
size is at most `min(1000, original row count)`; list/issue bounds are 50/20.
Oversize lookup/retirement also fail before HTTP. Large unsupported batches keep
their draft and original unresolved; no splitting/orchestration was added.

## Validation and independent review

Independent U1, Data/WPF and Core/wire/schema reviews are source-bound in the
private bundle. Findings about optional-field preservation, preview validation,
the full request envelope, late authorization, overlaps and three-level history
mapping were corrected before the canonical candidate freeze.

Targeted Data/migration/wire run 10 passed 149/151, with two newly added fixture
errors preserved in the TRX. Fixture-only corrections passed 3/3 in run 11,
including the two failed cases. Runtime source hashes stayed unchanged between
those runs. These receipts are not presented as a single 151/151 execution.
The migration/wire subset passed 107/107 and the static 0013 gate passed with pin
`475bd265683b005a91a24540358b05722f0f5bfaa9706fa48b48ee888ddef883`.
Historical migrations 0001–0012 and the three ordinary contract digests are unchanged.

Canonical candidate Core/Data passed 1385/1385, zero failed/skipped, with 49/49
required gates and solution/WPF x86 builds at zero warnings/errors. CLI,
backup/restore/failure and WPF image/lease/logging/paging checks passed. The serial wrapper uses
SDK 10.0.301, locked restore,49 required gates, full Core/Data, solution/WPF x86
builds, CLI/backup/restore and the existing WPF functional, image, lease, bounded
logging and paging smoke paths. The functional runner includes 19 scenarios;
the new recovery scenario contains 9 groups, all passed in the final canonical
recovery receipt. U1 also passed its 13 groups. The first new WPF accepted-race
fixture had invalid hash-derived UUIDs and incomplete ACK fields; its failure
was preserved and only those fixture fields were corrected after independent
review. No product guard or assertion changed.

The local functional sequence passed 18/19 scenarios. Its final existing
`PERF_visual_lifetime_and_public_commands` failed at the image-editor indicator
restart: a Background callback did not progress within the unchanged 3-second
guard. The same source path is byte-unchanged from PR121; an isolated PR121
baseline run passed. That does not establish the cause of the candidate failure.
Direct candidate isolated launch was rejected by automatic policy review
(`blocked by policy`, no more specific reason); no bypass or passing result is
claimed. The failure and baseline remain separate evidence. Exact-SHA hosted CI
must independently qualify the full functional sequence before integration.
Local SQLite and loopback evidence
does not certify an authenticated deployed Admin or physical hardware.

The first hosted Security run identified nine false positives for one synthetic
correction idempotency identifier in seven JSON fixtures. The coordinated Admin
variant uses a shorter synthetic ID and recalculates its canonical response
hashes with the unchanged parser. Fixture byte pins are updated; ordinary
fixtures and product code are unchanged. Only the nine exact historical
commit/path/rule/line fingerprints are excepted, following the existing policy;
default scanner rules and scanned paths remain unchanged. The failed run is
retained and a successor SHA requires fresh CI/Security qualification.

## Measurements and their limits

The measured host is MIN-ASUS, Windows 11 Home Single Language 10.0.26300 x64;
WPF is a net48 x86 process on CLR 4.0.30319.42000. Compatibility API reporting
Windows NT 6.2 is not evidence of Windows 7. All four supported languages are
checked at 1024x768 logical size (DPI 2 screenshots are 2048x1536 pixels).

The final recovery smoke separates 5000-row list/prepare/virtualization and
controlled oversize rejection from a 1000-row actual local commit/verified backup.
The 1000-row commit/verified backup took 344.669 ms with two existing-product
batches, one product write and one history write, all off Dispatcher. Its
replacement contains all 1000 rows (377561 saved UTF-8 bytes), with the original
still unresolved until ACK. The wider workflow observed 62 connections; it is
not a two-query claim. The 5000-row payload (1105961 bytes) was rejected without
backup or writes and realized only 21 visual rows.

Scheduled 50-ms samples observed these maxima:

| Workload | Memory samples | Private MiB | Managed MiB | Dispatcher samples / max ms |
|---|---:|---:|---:|---:|
| Plain three-row control | 34 | 199.21 | 19.85 | 28 / 222.238 |
| Five recovery reopenings | 39 | 198.54 | 17.17 | 32 / 117.804 |
| 1000-row list/verify/commit | 21 | 283.77 | 100.75 | 12 / 227.367 |
| 5000-row list/verify/reject | 22 | 226.67 | 55.33 | 15 / 217.698 |

These are sampled maxima, not guaranteed lifetime peaks. The cart diagnostic
192 MiB private / 32 MiB managed / 200 ms comparisons concern a different
workload; several observations exceed them. No memory/stability qualification
or budget increase is inferred. A previous 5000-row commit predates the final transport guard and remains
historical diagnostic evidence, not supported final transport qualification.

Absolute closed-window release is NOT_QUALIFIED. Repeated recovery is compared
with the same plain WPF control, with observed managed growth and Dispatcher
roots recorded separately. An automation-peer reference path to a closed recovery
window was found in the historical dump; its external native owner was not
identified. Control retained five windows versus four recovery windows;
managed memory changed 10993052 to 8947480 bytes, observed closed Dispatcher
roots and dropped observations were zero. The existing five-cycle growth check
passed. A comparative PASS must not become an absolute leak-free claim.
No unrelated cart budget or Win11 measurement qualifies Win7 memory or latency.

## Admin, readiness and physical acceptance

The minimal Admin extension is integrated by
[PR132](https://github.com/XNIW/merchandise-control-admin-web/pull/132), head
`9cc0f69122120430533c3e39bc9b91f45988bbec`, merge
`62f513f6ca15662e6bbb384a7510977fe68e1267`.
Candidate CI 37842949235 and Cloudflare 37842949240 succeeded; staging E2E and
staging/production deployment were skipped. The two new migration SQL files
and staging deployment require their separately reviewed authorization; merging
source does not assert that either has been applied.

The previously served Worker came from a staged checkout without a matching
original build commit. A minimal staging candidate based on the equivalent
served-tree commit `f401fc` is being prepared; its first foundation CI failed and
is under diagnosis. It requires its own review and gates before a concrete deploy
proposal. Admin main `62f513` is not treated as compatible merely because its
gates pass. All `cloudflare.yml` effects include the TASK150 secret update and smoke probes.
Current runtime provenance, applied schema and actual HTTP503/CPU/memory
telemetry must be refreshed for the final acceptance window. Empty metrics or
invocation errors=0 do not prove HTTP503=0.

According to the collected Mac receipt, TASK150 read-only run 37835676407 found
exactly one target shop, zero integrity
violations, duplicates or invalid rows. Its scope is `authorized_shop_plus_legacy`.
The original private TASK148 manifest/procedures were recovered, hash
`eca9f9158bf5b026ff6cd59c875cee1fbb158e6608ee0e915c5aa70abfdee892`.
The original private file hashes are Mac-reported, not a local byte verification.
Historical residual/in-flight zeros do not establish current articles authority,
`activeQaRuns=0` or `qaScopeClean=true`. Current exact-ID ownership, coverage and
concurrency remain separate from the image registry and historical checker.

No READY/runId is reserved or fabricated. Issuance requires the new client pack,
the consumer's exact 22-field schema, maximum 2-hour lifetime and deployment
verification within 15 minutes. The existing live runner then acquires its own
verified pack in a fresh directory. Economic C08 and restore C12 remain separate
TEST scopes. Mobile does not block POS/Admin; selected Android is
`XNIW/MerchandiseControlSplitView` Room 22, with source/installed/live/latency
evidence kept distinct.

Asus inventory found no current Win7 bridge/RDP/share. The Mac/Win7 target was
not probed, and a physical operator is NOT_OBSERVED.
The new package checklist is prepared for installation/upgrade, offline start,
scanner/focus, TEST sale/refund/void/reprint and physical paper, import/recovery,
restart/reconnect, display R2, real-destination backup failure/retry and guarded
TEST restore. Environment report must precede qualification. No SDK is required
on the till. An authorized business copy is also needed for historical audit;
no history is reconstructed from current prices.

Final receipt must distinguish integrated, generated, installed and observed
results, and list only concrete remaining resource/operator actions. CI, a
synthetic display or a printer queue alone cannot close live/physical acceptance.
