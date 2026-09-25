# Win7POS post-PR109 residual closeout

Task ASUS-W7POS-017 starts from `be370ba31b7cc5fa79d9fb04263fbdc336dcbb26`.
ASUS-W7POS-016 and F01–F12 remain delivered. This is a continuation, not a
repeat audit. Final SHA, workflow run IDs and artifact hashes are attested
outside this commit to avoid a self-referential SHA update.

## Scope and current findings

| Area | Classification at intake | Result |
| --- | --- | --- |
| A. Published application | PR109 already merged; initial local/origin main identical, 0/0 | PASS; no application change is justified by a preflight denial |
| B. Article staging | Historical readiness superseded; zero logical runs | NOT_EXECUTED; new Admin readiness and supported fresh-run contract required |
| C. Image recovery | Existing encrypted checkpoint, cleanupPending=true | NOT_EXECUTED; preserved, no retry of denied authority |
| D. Performance | Short measurements do not establish retention | Stage instrumentation and finite soak added; measured results below |
| E. Installer | Release Pack exists, installation requires authorized QA host | Download/integrity work below; install/upgrade/uninstall NOT_EXECUTED |
| F. External qualification | Win7/peripherals and process-start permission absent | NOT_EXECUTED; one owner execution card below |

There were **56** worktrees at intake (55 other checkouts), all clean. Paths,
heads, branches and statuses are inventoried privately and must remain
unchanged outside the delivery checkout. This supersedes the previous count
of 54 other worktrees without deleting or incorporating any of them.

### P109-P01 — Release Pack omitted a checksummed file

Reproduction: download [Release Pack run 36181404999](https://github.com/XNIW/Win7POS/actions/runs/36181404999),
extract its payload ZIP into `Win7POS/` under the downloaded artifact root,
and invoke `scripts/win7pos/windows/test-protected-release-artifacts.ps1`.
The canonical verifier fails: `A checksummed release artifact is missing`.
The sole missing checksummed path is `Win7POS-build-report.md`.
The workflow generated and checksummed that report, but its upload path list
omitted it. The patch adds that existing file to the ReleasePack upload list.
No checksum, provenance or signature validation is weakened.

All 39 downloaded payload paths independently match the canonical normalized
manifest. `VERSION.txt` uses the validator's documented BuildTimestampUtc-only
normalization; comparing its raw hash to that normalized hash is invalid.
The downloaded GitHub archive, embedded payload ZIP, Setup EXE and prior local
candidate are distinct files and retain separate hashes in the attestation.

## Staging prerequisites and preserved state

Admin repository: [XNIW/merchandise-control-admin-web](https://github.com/XNIW/merchandise-control-admin-web).
Fetched Admin origin/main: `918e8e1cbe2d286c3a81b59ad586d7031fac9a78`;
the existing local checkout is preserved. Its current handoff explicitly
withdraws the old readiness. TASK-148 is user-confirmed closed. TASK-150 is
paused and preserves its prior evidence; neither is reactivated here.

The article wrapper still checks literal historical runtime `9fb54f50`,
deployment `5ad3652d` and version `57af0535`. A current generic readiness
schema/issuer for a new POS run was not found in the authoritative Admin
handoff. Removing those checks or authoring an unapproved READY document
would not establish staging authority. A replacement must be agreed and
tested with the Admin owner before the client can consume it. No fresh
article runner was invoked with the known-invalid prerequisite.

Admin's current master-plan receipt reports selective staging source
`c55f88a36ac89684f25fd503ca7a3bc085c08660` and Worker version
`6343d39c-d50a-4c88-89df-676f709697a2`, distinct from Admin main. An actual
read-only `wrangler deployments list --name merchandise-control-admin-web-staging
--json` was attempted with the existing local Wrangler. It returned missing
noninteractive Cloudflare authentication. Therefore these are **reported**
deployment values, not a freshly verified deployment. No temporary account,
alternative privileged credential or deployment was used.

The canonical local QA credential validator passes version 2, restricted ACL,
DPAPI decryption and staging host checks. That says nothing about remote actor
authorization. The image denial is `boundary_result_issue_failed_bootstrap_actor_denied`.
Current server code checks runtime lease, shop/device/session/staff identity,
credential versions, both secret hashes and `catalog.write`; the error does
not isolate which check failed. Expiry alone is not proven.

The existing 1,926-byte DPAPI checkpoint remains in its required QA directory.
Its hash and directory inventory are saved privately; neither checkpoint,
database, credentials, scope identifiers nor manifest is published.
The last diagnostic has cleanupPending=true, zero completed controls and no
new matrix. Remote scope/ownership/count verification requires the denied
canonical boundary; it has not been replaced by direct SQL or other endpoints.

## Performance protocol and reproducibility

Run Release net48/x86 on Asus Windows 11 with SDK 10.0.301. Build and tests are
serialized. Use new output directories outside the repository:

```powershell
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -OutputDirectory C:\QA\post109-20k -Products 20000
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -OutputDirectory C:\QA\post109-100k -Products 100000 -SoakMinutes 60
```

`-HarnessDirectory` can select a harness overlaid with exact downloaded
package assemblies. The runner records and rechecks every EXE/DLL hash,
rejects reused evidence directories, bounds process duration, and checks
sample counts and soak duration. It runs the existing QA harness, not the
previously denied production process-start benchmark.

Preserved baseline: 20k/100k products, ten price-history rows per product,
carts 1/10/50/100/500. Sample 0 is the first invocation; warm samples 1–30
are summarized separately with nearest-rank p50/p95 and maximum. All samples,
including outliers and incomplete probes, are retained. The stage CSV adds
service, snapshot application, layout, bitmap allocation and bitmap rendering.
These are cumulative measured work stages in one process, not an OS-cold
launch or physical monitor paint measurement; JIT/GC remain included.

The soak repeats a 500-line cart, ten scans in rows and grid modes, discount
dialog open/close, real product-image presenters/editor with synthetic image
states, and a 20-second idle interval. It records private/managed bytes,
handles, GDI/USER objects, threads, cumulative GC counts, Send-priority
dispatcher probe, observed pending dispatcher operations, image metadata
cache/lookups and scan/render timings. No forced GC is used. Image presenter
coverage is local UI lifetime coverage, not remote transfer/cache acceptance.

A preliminary probe with a Background-priority dispatcher observation stalled
on the occluded desktop and was terminated with its partial evidence preserved.
The corrected probe uses the same Send priority as the existing scan benchmark;
it completed three cycles in 88 seconds. This probe is not the qualification run.

## Installer and operator execution card

The Inno script requires administrator rights, .NET Framework 4.8 and VC++ x86,
targets Windows 7 SP1+, writes Program Files/shortcuts/uninstall registration,
and intentionally preserves ProgramData. A separate data folder does not
isolate installation effects. No authorized VM/snapshot or QA installation
target has been provided, so no install/uninstall is attempted on Asus.

Execute the following only with the named owner/environment authority, then
attach one redacted receipt per row to this task's final attestation:

| Owner / environment | Required action and evidence |
| --- | --- |
| Admin maintainer, staging | Confirm the actual deployed source SHA, deployment/version and supported POS contracts through authenticated Cloudflare read access. Issue a new readiness for the final client SHA, new run ID, isolated shop/scope manifest, clean preflight and expiry; do not edit the superseded marker. Review/test any new producer and consumer contract in their repositories. |
| Shop/QA authority + Admin maintainer, staging | Explicitly authorize rehabilitation of the original image bootstrap actor through the administrative procedure. Verify lease, identity, credential generation, catalog.write and environment. Then run the existing `Invoke-Win7PosProductImageStagingAcceptance.ps1` against its preserved directory: first recovery/cleanup, verify terminal receipt/counts/ownership/idempotence, then a distinct new matrix. No synthetic sales in article/image scope. |
| QA VM/snapshot operator | Preserve the old candidate and snapshot; verify final artifact hashes/signatures, install clean, launch, save synthetic settings/DB, install upgrade from previous candidate, verify persistence, uninstall and verify retained data. Record registry/shortcut behavior, logs, installed hashes and rollback. |
| Win7 SP1 operator | On QA data verify net48/x86/SQLite, HTTPS/login, offline/online and restart, real scanner/focus/quantity/discount, one synthetic sale, return/void/reprint without duplicate stock or sale, interrupted backup/restore, DPI and monitor reachability. Record OS/hardware/driver and final hashes. |
| Printer/drawer owner | Give explicit operational consent; verify driver/spooler and Notepad first, then one bounded TEST receipt and error behavior. No automatic drawer pulse. |
| Environment policy owner | Explicitly approve the previously rejected production launch/close benchmark before it is run. Until then process-start cold/warm remains NOT_EXECUTED; a warm start is never labeled OS-cold. |

No new owner-accepted deferral or production qualification is inferred.
GitHub delivery and staging/hardware acceptance are separate outcomes.
