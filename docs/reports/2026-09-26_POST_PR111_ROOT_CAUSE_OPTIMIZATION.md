# Post-PR111 root cause optimization — ASUS-W7POS-018

Baseline: `bedcf17a97d814a3b098387cc2720ff2b11abbbf` (PR109–111).
Status: implementation and short causal verification in progress; **not yet
qualified**. The historical b794 soak remains STABILITY=FAIL. Final merge,
package binding and external attestation are pending.

Resume on an unlocked desktop (2026-09-27 UTC): exact-head CI and security for
`0ed0a061f5d85f3e21097d11c0d5161b5444ccab` passed, including all nine functional
scenarios. The same complete interactive suite then passed on Asus, including
scanner focus and Unicode composition. The first Integrated attempt nevertheless
failed at cycle 0, Rows scan 3 with `qualification_visual_scan_timeout`, before
completing a cycle. Input/thread desktop matched and QA had foreground in every
recorded sample. A short observer-off ablation reproduced that same third-scan
timeout. Thus the remaining interactive failure is **not resolved** by restoring
the desktop and is not caused by the bounded observer alone. No threshold was
changed; no merge or final qualification is justified by this attempt.

Failure-only collection in v16 retained 1,177 pending Background UpdatePeer
callbacks plus focus/text/caret/render work. Native PM_NOREMOVE observed dispatcher
message `0xC215`, with no keyboard or mouse queue indication. This identifies the
pending work, not the callback consuming the interval. A new v17 trace-enabled
harness was then blocked before process creation by Smart App Control: event
3077, `VerifiedAndReputableDesktop`, status `0xc0e90002`, executable SHA256
`01836d90abfa71d84bb6d9b9b2f2f5427d2243ff0b5ee396692b2d89f6795550`.
The matching 3118 event and source/binary are preserved privately. No policy
change, renamed copy, alternate launch or replacement collector was attempted.
At that checkpoint the new trace path was only compiled; it has since run on the
independent hosted QA channel described below, but not on Asus. Qualification
rejects trace mode. Collection runs only after failure unless explicitly tracing
in Diagnostic mode, and does not change the measured acceptance limits.

The [interactive attempt summary](evidence/2026-09-26-post-pr111/interactive-attempts.json)
and partial CSVs retain the failed runs. Their final flags are
MEASUREMENT_COMPLETED=false, ENVIRONMENT_VALID=false, STABILITY_PASS=false:
valid early environment samples do not certify an incomplete run. The previously
completed diagnostic control remains separate. Further causal work requires a
QA harness accepted through the Asus host's authorized approval/signing channel.

## Authorized hosted QA and remaining Asus reproduction

The existing public-repository `windows-latest` WPF CI runner is an available
development QA channel with its own execution policy. An explicit manual option
in the existing CI workflow builds source independently and calls the existing
performance runner serially. No denied Asus executable was transferred, retried,
renamed or loaded through another mechanism. No certificate, paid service,
account, host security configuration or production release was created.
The skipped full-CI job in this manual workflow is named `canonical-not-requested`;
it does not substitute for the separate PR `build-and-check` job.

[Run 36289638541](https://github.com/XNIW/Win7POS/actions/runs/36289638541)
at `554743b1e0a30f6d2e1dd39e0b88b6712386ed65` completed five public scans with
and without trace. [Run 36290265761](https://github.com/XNIW/Win7POS/actions/runs/36290265761)
at `08b96fc6adf798c1f5fb87e868c3a11363372ba7` additionally compared dispatching
the same public command at Input. Each run starts with 100k products and 500
persistent cart identities; all five increments completed, quantity reached six,
and collection changes stayed zero. Each result is **M=true, E=true, S=false**:
these are short Diagnostic runs, not Integrated or Final qualification.

Both machines are Microsoft VMs with 16GiB RAM, four logical CPUs, image
`win25-vs2026` / `20260922.246.2`, AC power, matched Default desktops and their
own QA window foreground. CPU models differ: EPYC 7763 in the first run and
EPYC 9V74 in the second. Comparisons below stay within each machine. They use
96 DPI versus Asus's 192 DPI. CLR/WPF file versions match Asus, but this does
not establish equivalent rendering, scheduling, input state or hardware latency.
UI Automation reports clients listening in both traced runs; its presence alone
does not reproduce the Asus timeout.

Full UI includes service, return to UI, apply, command overhead and layout;
bitmap cost stays separate. Every cold sample is retained:

| Hosted run / variant | Scan 1 | Scan 2 | Scan 3 | Scan 4 | Scan 5 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 36289638541 / untraced | 322.205 | 37.913 | 69.500 | 47.315 | 64.603 |
| 36289638541 / traced | 244.183 | 54.054 | 48.148 | 52.559 | 37.966 |
| 36290265761 / untraced | 261.197 | 39.523 | 30.502 | 49.038 | 30.473 |
| 36290265761 / traced | 162.823 | 37.290 | 31.976 | 48.920 | 28.529 |
| 36290265761 / traced Input dispatch | 262.708 | 33.494 | 40.534 | 29.509 | 36.780 |

Values are milliseconds; five samples cannot establish useful tail percentiles
or a stability PASS. [Raw samples, provenance and executable hashes](evidence/2026-09-26-post-pr111/hosted-diagnostic-summary.json)
are preserved, with the complete timelines in the downloaded private artifacts.
No Asus before/after improvement is inferred from these separate hosts.

The trace captures executed-operation start/end, application checkpoints and
a 100ms thread-pool watchdog independent of the UI dispatcher. Capacities are
bounded; both hosted comparisons record zero dropped trace entries. First-run
render work spans 150.769ms with about 7MB allocated and 156.25ms AppDomain CPU;
the watchdog observes that running callback while the UI is occupied. These
counters include workers and are not exclusive CPU stacks or an attribution of
the missing Asus interval. Timer file I/O shares the diagnostic observer lock,
so traced timing has overhead; the separate untraced control remains necessary.

The new checkpoint demonstrates `DispatcherSynchronizationContext` priority
**Send** in the startup-derived driver, and **Input** in the explicit comparison.
The earlier description of this as a Normal-only continuation chain was too
narrow. WPF's [context implementation](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/DispatcherSynchronizationContext.cs)
posts with its captured priority; the observed runtime value is the evidence
for this harness. The Input variant preserves enqueue latency inside the same
end-to-end clock: its first scan includes about 252ms command overhead. Neither
variant reproduces the third-scan timeout here, so this is a diagnostic control,
**not a proven fix or a change to qualification protocol v3**. Internal WPF work,
IME, accessibility and timeouts remain unchanged.

For the ordinary-product path under test, `IsBusy=false` follows the awaited
service and applied snapshot, then the command requests scanner focus. The
harness now requires an observed true-to-false transition, the exact quantity
increment and a completed service timestamp before probing Input. That verifies
economic completion without treating it as completion of queued focus/render
work. The 250ms visual probe remains distinct from the 10s public-command limit.
New runner contract cases reject short qualification, tracing in qualification,
Input comparison outside short Diagnostic, mixed short/soak and mismatched
Integrated SHA before any process launch: 32 validator vectors plus six runner
preflight cases pass; all 49 canonical gates and the x86 build pass locally.

Final review also reproduced a validator omission: a warm scan with 101ms visual
wait against a synthetic 100ms visual budget was accepted when its full UI time
remained below 250ms and owned focus/scroll callbacks stayed fast. The validator
now checks each warm scan's `focus_scroll_wait_ms`, rejects missing/non-finite
values and disallows a visual budget above the already-required 250ms ceiling.
Four focused negative cases close this false-positive path. The frozen budget
file and measured samples are unchanged; this does not fix the Asus timeout.

The structural regression additionally asserts different card heights, public
quantity/identity/selection after recycling, and real Grid presenter product
binding with removal of a synthetic previous-product image after container
reuse. It exercises actual view templates and public commands. It is not an
image download/decode, physical scanner or installed-IME certification. Runtime
results of the consolidated suite belong to the exact-head CI run recorded in
the external attestation, rather than being inferred from a predecessor.

## Causal ledger

| Finding | Supported cause and discriminating evidence | Change and regression |
| --- | --- | --- |
| PERF-LATENCY | Resetting 500 identities each cycle repeatedly realizes all 500 hidden Grid cards. Removing only the hidden Grid ItemsSource eliminates multi-second Rows stalls; restoring it reproduces them. A visibility guard alone does not. Ordinary scans have zero collection changes. | Replace eager WrapPanel with a recycling, variable-height, multi-column `VirtualizingCartWrapPanel`. Preserve card width, selection, scrolling and resize. Runtime regression checks attached visible containers at indices 0/250/499, four widths, both views, removal/empty/re-add and public commands. Corrected an initially detached recycled-container implementation before accepting measurements. |
| PERF-QUEUE | 501 invisible ProgressBars remain indeterminate. Original mixed control leaves 96 operations, oldest 38,262ms after four cycles; disabling only invisible indicators leaves two, oldest 18ms. Reintroduction reproduces accumulation. Producer stacks identify TextEditor.OnTextViewUpdated during arrange. | Bind IsIndeterminate to actual IsVisible in the cart busy indicator and ProductImagePresenter. Test loading-visible, ancestor-hidden and completed-image states. Do not abort, reprioritize or remove internal WPF operations. |
| PERF-MEMORY | Pending Background InitTextStore delegates retain TextEditor._uiScope → TextBox → dialog parents → closed ProductEditDialog → VM. Animated rendering repeatedly allocates while the controls are invisible; removing that work drains the demonstrated root chain. Eager card trees also retain 500 control subtrees. | Same two application fixes. Observe natural GC, post-idle heap/private bytes, known closed-window root chains and decoded cache pixel bytes. No forced GC. This establishes those paths, not an exhaustive heap census. |
| PERF-VALIDATION | Previous runner verified process/sample/duration completion but no stability predicate. Legacy observer scans a weak-reference list on completion; replacement has fixed capacity and O(1) normal completion. Replacing constructor VM disconnects real focus wiring. Reset stress and chained high-priority continuations are harness artifacts. | Preserve real PosView composition, persistent identities, alternating mode order, explicit Input completion inside scan timing, separate bitmap timing, bounded observer and three independent result flags. Synthetic negative vectors cover stalls, partial recovery, invalid/missing data, environment, timeouts, roots, overflow and visual waits. |
| INTERACTIVE-RESIDUAL | Asus cycle 0 / Rows scan 3 times out even without the observer, with valid foreground/desktop samples. Pending UpdatePeer callbacks do not identify the executed blocker. Hosted controls do not reproduce it. | No residual production fix claimed. Bounded executed-operation timeline and short public-scan controls are available; Asus attribution and equivalent before/after remain missing. |
| QA-EXECUTION | SAC rejects v17 before process creation; separate from the earlier running-process timeout. Existing signing assessment finds no configured trusted channel. | Independent hosted source builds now execute successfully under their own QA policy. Asus acceptance/signing remains external; no bypass, merge or qualification exception. |

Equivalent visual requests also had an independently reproduced defect: 250
RestoreScannerFocus calls posted 250 Input operations. PosView now keeps at
most one owned focus and one owned selection-scroll operation, checks active
window/visibility and cancels only its own pending work on unload. The public
scan path still executes every economic command. Regression verifies quantities,
row identity, scanner event wiring and no focus theft from an active editor.

## Evidence and limits

Private source versions, payload manifests, full diagnostic outputs and root/
producer traces are retained in `C:\Dev\Win7POS-post-pr111-20260926`.
The PR111 controls overlay all 41 files of the downloaded baseline payload;
patched diagnostic variants replace only the application assembly and record
its new hash separately. Earlier candidates, checkpoint and 55 other worktrees
are preserved. All 56 worktrees were clean at intake.

- Fixed Rows without reset: after cycle zero, UI-return maxima below 62ms;
  with reset, recurring roughly 1.8–2.3s stalls and 500 hidden Grid containers.
- A roughly 1.85s render callback coincides with about 145MB of AppDomain
  allocations; repeated animated callbacks coincide with 3–4MB each. These are
  operation-interval CPU/allocation counters, including concurrent worker work,
  **not exclusive sampled CPU/allocation stacks**.
- Native observations identify posted dispatcher-processing messages. They do
  not identify keyboard/IME input as the cause. IME/TSF/accessibility remain on.
- The initial dialogs-only variant still had a populated cart and is excluded
  as an isolated control. Corrected bare-dialog control has no cart view: four
  cycles stay around 8.7–13.6MiB managed heap and 5–6 pending timer operations.
- Early v7 virtual-panel timing is provisional and excluded from acceptance:
  functional review found recycled containers detached after scrolling. The
  generator can return an existing recycled container requiring visual reattach.
- Canonical visual regression found another first-layout defect in the new
  panel: an already-populated cart accessed the generator before it existed.
  Initializing `InternalChildren` first fixes the canonical reproduction;
  all 50 visual captures pass afterward. This follows the initialization
  contract in Microsoft's [Panel source](https://source.dot.net/PresentationFramework/System/Windows/Controls/Panel.cs.html)
  and [VirtualizingPanel source](https://source.dot.net/PresentationFramework/System/Windows/Controls/VirtualizingPanel.cs.html).
  A small-rectangle/tall-card regression also preserves focus/accessibility
  visibility when a card exceeds the viewport height.
- Authentic public-command batches generate about 9k transient Background
  UI Automation UpdatePeer operations. A 4,096-entry observation table overflowed;
  those runs are invalid measurements. Capacity is explicitly 16,384, peak and
  dropped counts are recorded, and any further overflow invalidates qualification.
  This is observer capacity, not a widened stability threshold.
- An uninterrupted chain of higher-priority continuations postponed focus/scroll through
  a synthetic 20-scan batch. Protocol v3 yields at Input for each completed public
  command and includes the entire wait in `command_overhead_ms`; the separate
  `focus_scroll_wait_ms` is a subcomponent and must not be added twice.
- WPR validated the process-filtered CLR profile but Windows denied start with
  `0x80070005 Access is denied`. No elevation, policy change or alternate denied
  collector launch was attempted. ETW/full heap dumps remain unavailable.
- WPF Unicode composition exercises `中文 café áéí`; it is not physical installed
  IME, scanner or Windows 7 hardware qualification.

## Measurement contract

The [scale CSV](evidence/2026-09-26-post-pr111/scale-comparison.csv), individual
scan/idle CSVs and [hash provenance](evidence/2026-09-26-post-pr111/scale-provenance.csv)
retain equivalent four-cycle reset stress (cycle zero preserved, cycles 1–3
summarized; 30 samples per mode). These are diagnostic, not desktop qualification:

| Products / rows | Rows p50 before → patched | Rows p95 before → patched | Rows maximum before → patched | Scan 2 median before → patched | Scan 3 median before → patched |
| --- | --- | --- | --- | --- | --- |
| 20k / 500 | 60.78 → 8.41ms | 3422.55 → 192.26ms | 3422.59 → 245.38ms | 3422.55 → 12.81ms | 17.24 → 8.12ms |
| 100k / 500 | 57.05 → 9.47ms | 3394.40 → 186.64ms | 3693.49 → 192.82ms | 3106.70 → 12.19ms | 33.14 → 13.02ms |

Grid realization falls from 500 to at most 11 containers in these controls.
All final scale comparisons use the same bounded observer and diagnostic
protocol. The earlier 100k control had observation off; it was repeated with
matching observation before accepting this table. The
[intermediate v12 summary](evidence/2026-09-26-post-pr111/scale-comparison-v12-provisional.csv)
is retained, including its 20k reset-stress p95 of 259ms. Its complete raw samples
remain private; the table above uses the corrected v14 panel, not that earlier
prototype. Short-run variation is not attributed solely to the last layout
corrections. Reset stress stays separate from persistent-identity public-scan
qualification, and none of these offscreen runs certifies monitor latency.

With identical protocol v3 at 20k, observer-on/off warm full-UI medians are
88.06/81.03ms, p95 131.69/157.97ms and maxima 154.35/199.94ms (80 warm samples
each). This short comparison does not establish zero observer overhead, but
does not reproduce multi-second stalls. Observer-on peak is 9,558 entries,
zero dropped. Both runs have ENVIRONMENT_VALID=false because the input desktop
cannot be matched and QA has no foreground. See the
[observer comparison](evidence/2026-09-26-post-pr111/observer-comparison-v12.csv).

Before the desktop was unlocked, the structural regression passed public remove/re-add, empty-grid and
new-scan checks before failing the keyboard-focus assertion in that same
noninteractive environment. Earlier interactive v9 focus/composition checks
passed, but are not substituted for current or final-payload qualification.

`run-cart-performance.ps1` defaults to Diagnostic and cannot emit stability
PASS in that mode. Qualification requires a preregistered budget, exact payload
binding and an enabled observer; Integrated requires at least 10 minutes,
Final at least 60 and a 40-character merge SHA matching the binding. Output must
be a new directory. Application/dependency/harness hashes and budget are checked
before/after; all results retain MEASUREMENT_COMPLETED, ENVIRONMENT_VALID and
STABILITY_PASS with specific reasons.

The isolated service benchmark preserves sample 0 and 30 warm samples for
1/10/50/100/500 rows. The integrated workload uses 500 persistent line identities,
20 public scans per cycle, alternating Rows/Grid order, discount/image windows,
separate RenderTargetBitmap work and 20 seconds idle. SQL metrics count batches
and dispatcher work; `product_lookup_update` includes lookup and session update,
`snapshot_query_map` includes query/mapping, not pure SQL execution time.

The environment records UTC, awake monotonic time excluding suspend, CLR/WPF,
input desktop, WTS session, foreground/visibility, 192-DPI host samples, power
and suspend/session events. Offscreen rendering is not monitor latency.
Host inventory is Windows 11 Home Single Language 10.0.26200 x64;
the QA process is x86, CLR 4.0.30319.42000, Framework file version 4.8.9345.0 and
WPF file version 4.8.9347.0. `Environment.OSVersion` reports compatibility version
6.2.9200.0; the runner records the installed OS via Win32_OperatingSystem separately.
A locked/different desktop remains ENVIRONMENT_VALID=false; it is not repaired
by changing host policy or repeatedly forcing foreground.

Post-idle observations include ready versus inactive operations, eligible and
posted ages, owned focus/scroll peaks and maximum wait over the whole cycle,
known closed-window roots, handles/threads/GC, metadata entries and live decoded
pixel storage. Pixel storage is not total bitmap object cost. Probes have bounded
timeouts; timeout/overflow/unsupported reflection fields are insufficient evidence.
Memory validation examines every rolling window after warm-up, including peaks
that partially recover, in addition to absolute ceilings.

The 614.903-second clean diagnostic control completed 26 cycles. After two warm-up
cycles: managed heap 14.34–17.16MiB, private peak 146.67MiB, ready queue 0–3 with
oldest eligible age 26.051ms, zero observed closed-window roots/dropped records.
Maximum increase of a five-cycle managed-heap mean over the first such window
is 383,019 bytes (0.37MiB); no GC was forced. Public Rows/Grid UI p95 is
145.352/139.345ms and maximum 222.866/189.171ms, with bitmap p95 separately
18.555/19.430ms. Isolated service p95 at 500 rows is 9.640ms and Send p95 0.176ms.
These are **not qualified desktop latencies**: all three flags remain
MEASUREMENT_COMPLETED=true, ENVIRONMENT_VALID=false, STABILITY_PASS=false.

The [preregistered budget](evidence/2026-09-26-post-pr111/preregistered-budget-v1.json)
was written after this diagnostic control, before any integrated/final
qualification. SHA256: `63970e308d0c0ee2e2e62504c5828f5863233a74e369bc63950714787d083398`.
Fixed latency budgets are unchanged: service p95 <=50ms, Send p95 <=16ms,
full public scan plus UI/layout p95 <=250ms and maximum <=1000ms. Other limits:

| Metric after two warm-up cycles | Preregistered maximum |
| --- | --- |
| Ready queue / oldest eligible age | 8 / 250ms |
| Owned focus and scroll | One pending per category, none after idle; cycle maximum wait 250ms |
| Input/Render/DataBind/Background probes | 200ms; any timeout invalidates evidence |
| Private / managed bytes after idle | 192 / 32MiB |
| Managed five-cycle window growth | 8MiB above the first warm window |
| Live decoded cache pixels for this synthetic workload | 8MiB |
| Metadata entries / realized Grid containers | 512 / 32 |
| Closed-window roots / observer drops | 0 / 0 |

The margins allow observed natural GC/native and scheduling variability, without
using a first/last-only comparison. Control cache pixels were zero; the 8MiB
limit is a synthetic-workload allowance, not certification of a fully populated
shop image cache. The clean control is noninteractive; environment validity
remains a separate mandatory condition for future qualification. A retrospective
validator check of the control has no numerical stability reasons but remains
nonpositive for environment; it is not a preregistered qualification run.

## Scope and validation

Application changes are limited to PosView (selection/focus and Grid panel),
ProductImagePresenter (invisible animation), and opt-in stage instrumentation
in implicated AddByBarcode/BuildSnapshot/ApplySnapshot methods. The measurement
scope has no SQL text, barcode logging or production I/O and is inactive normally.
The test helpers separate factorial reproductions, functional regression,
environment and bounded observation from qualification orchestration.
The runner, validator and canonical gate connect these checks to existing CI;
no long soak is added to every commit.

Serial locked restore, 49/49 gates, solution/WPF/harness builds, 1,033/1,033
Core/Data tests (zero skipped), CLI self-test and image profile/serialization
smoke pass. After the panel first-layout correction, all 50 visual captures and
eight existing functional scenarios pass. The new structural/public-command
regression reaches its focus phase, then fails with `hostActive=False`,
`hostVisible=True`, `viewLoaded=True`; its preceding recycling/structural checks
pass. The functional runner therefore correctly remains nonpositive overall.
Authorization lease (including restart/capacity), 100k bounded logging and 100k
product paging also pass. The validator has 28 passing positive/negative vectors,
including wrong dataset/cart receipts, numerically duplicate sample identifiers,
non-finite required duration and the old soak rejection. These initial local
focus failures were followed by successful exact-head CI and unlocked-Asus
functional checks on `0ed0a06`, as recorded above. The subsequent Integrated
attempt failed; valid integrated and downloaded final-package qualification
remain pending. No final performance PASS is claimed.

Next authorized sequence: use the available hosted diagnostic channel for
independent regressions; activate an accepted Asus QA harness through the owner
channel below, identify and correct the remaining interactive third-scan timeout,
then pass a 12-minute Integrated qualification against the
unchanged preregistered budget; then exact-head CI/normal merge, download and
validate the merge Release Pack, repeat 20k/100k measurements and run at least
60 useful minutes of Final qualification. Neither the remaining timeout nor
the executable policy block is an accepted exception; no final soak has started.

## External prerequisites — checked once

Admin origin/main remains `fe4907adc51ff842720e1c7eb36aa05e0fa53cb8`;
TASK150 remains PAUSED_FOR_WECHAT_006_STAGING_HANDOFF, with no new readiness.
The image acceptance DPAPI checkpoint is unchanged (SHA256
`d3d904475376bc9f0df06044190a9da65e450006dd3abd8fc372a887892b0579`), cleanupPending
remains true. No cleanup/recovery was attempted. Owner must provide the authorized
readiness/recovery handoff, disposable installer VM, Win7/peripheral target and
explicit previously denied startup-benchmark authorization for those separate
paths. No production release/tag, production launch, printer/cash drawer action
or renewed staging denial attempt is part of this local performance work.

## Single activation card for the remaining external work

| Prerequisite / responsible party | Activation and verification | Resume point |
| --- | --- | --- |
| Asus QA executable acceptance / PC owner and authorized signing administrator | Supply a configured trusted signing procedure, or owner-managed official development-host configuration. The saved signing assessment is reused; no repeated certificate search. If signing is chosen, use staging, record hashes before/after, verify signatures and regenerate the actual payload binding. No policy change is embedded in project scripts. | Accepted current-head harness, short untraced/traced Asus reproduction; no retry of the denied v17 file. |
| Article staging and image recovery / Admin deployment and shop owner | Provide current typed readiness plus authorized recovery/cleanup handoff. No new readiness or cleanup is invented. Existing DPAPI checkpoint and `cleanupPending` remain unchanged. | Canonical staging/image runner only after local performance is stabilized. |
| Installer and Windows 7 / QA host and hardware owner | Provide disposable VM/snapshot for install/upgrade/uninstall and an authorized Win7 SP1 net48/x86 target. Scanner/IME and printer/drawer need their real hardware/driver checks and operational consent. | Exact verified merge package, economic/offline/restart/backup checks. Hosted Windows QA does not certify Win7. |

After the first row is satisfied, resolve `$HarnessDir` and `$Binding` from the
accepted, byte-verified current-head payload, and `$HeadSha` from PR112. Do not
reuse the old `0ed0a06` binding or its intentionally head-bound resume script.
Use a new `$ShortOut`, then the existing runner (trace enabled only for a separate
Diagnostic reproduction):

```powershell
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -Mode Diagnostic -DiagnosticScanCount 5 -Products 100000 -HarnessDirectory $HarnessDir -OutputDirectory $ShortOut
```

Verify five quantity increments, valid desktop/foreground, zero trace loss and
actual executed-operation attribution. A failure returns to that short
reproducer. Only after a demonstrated correction and the functional suite pass,
resolve new evidence paths and use the unchanged budget hash stated above:

```powershell
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -Mode Qualification -Stage Integrated -Products 100000 -SoakMinutes 12 -HarnessDirectory $HarnessDir -BudgetPath $Budget -PayloadBindingPath $Binding -ExpectedCommit $HeadSha -OutputDirectory $IntegratedOut
```

Require all three flags true before normal merge and exact-merge ReleasePack
download/verification. Final needs that new package's harness/binding and at
least 60 useful minutes. Neither Integrated nor Final has passed; Final has not
started. PR112 stays draft with no merge while the Asus residual remains open.
