# Post-PR111 root cause optimization — ASUS-W7POS-018

Baseline: `bedcf17a97d814a3b098387cc2720ff2b11abbbf` (PR109–111).
Software qualification: **PASS**. On 2026-10-01 the owner explicitly authorized
software closeout, normal PR112 merge after exact-final-HEAD CI/Security PASS,
and verification of the exact post-merge Release Pack. The owner accepted
external QA deferral; this decision supersedes the earlier requirement to run
Asus Integrated before software merge. It does not waive or change any test
threshold, measured failure, signature/policy control or physical requirement.

| Closeout item | Owner-approved disposition |
| --- | --- |
| Qualified source | `aea65314b9de6f18a46a5ba9aefbeb2de363012d`; no src/tests/scripts/.github differences to initial PR HEAD `4a4af8a9afaa7d2d3858e7323b63fc22655b59f9`. |
| Software evidence | Reuse 1,033 Core/Data tests, nine WPF scenarios, and hosted 12-minute + 60-minute qualification below; both M/E/S=true. Exact updated-HEAD CI/Security and merge-package integrity remain required. |
| ASUS-THIRD-SCAN | `OWNER_ACCEPTED_DEFERRED_EXTERNAL_QA`; historical scan-3 timeout preserved, root cause **unproven**, no correction or Asus PASS claimed. |
| Asus physical Integrated / Final | `NOT_EXECUTED / OWNER_DEFERRED`; no M/E/S results. A premerge hosted soak is not Asus Final. |
| Other external QA | Windows 7 SP1, Xprinter/spooler, barcode scanner, installer install/upgrade/uninstall, staging/image recovery: separately `OWNER_ACCEPTED_DEFERRED_EXTERNAL_QA`. |
| Production certification | `productionCertified=false`; software qualification and package integrity are not physical or production certification. |
| Scope and controls | Documentation-only closeout; no signing purchase, license change, SAC bypass, new local execution or budget relaxation. Preserve 55 unrelated worktrees and image checkpoint. |
| Delivery | Initial main `bedcf17a97d814a3b098387cc2720ff2b11abbbf`; exact final PR checks, normal merge and post-merge package verification pending at this documentation checkpoint. |

Historical status before the owner-deferred decision, independently verified on
2026-10-01: HOSTED-TIMER-AGE is resolved on
the hosted QA channel, with same-host causal removal/reintroduction and both
full qualification lanes passing on `aea65314b9de6f18a46a5ba9aefbeb2de363012d`.
Protocol-v4 fixture readiness is verified in three paired trials. Historical
Integrated on 2026-09-28 remains **M=true, E=true, S=false**; the distinct Asus
residual is still open. PR112 remains draft, with no merge or Final.
The historical b794 soak remains STABILITY=FAIL. Candidate package binding is
verified for the hosted run below; exact published-head delivery is recorded in
the single external interim attestation.

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

## FireTick investigation — 2026-09-28

The historical 733.084-second run was revalidated with the current validator:
M/E remain true and S remains false at cycles **3/12/13/17/18/23**. Its CSVs,
payload and frozen v1 budget bytes were not changed. No new Release Pack was
created to repeat that analysis. Product source remained unchanged through the
first four measured experiments. Candidate `b512cc1` tested the catalog loading
indicator and was **disproved** as the cause; that product change was reverted.
Candidate `a69b275` corrects the actually observed hidden animated control in
ProductEditDialog. Same-host removal/reintroduction confirms the application
cause; the consolidated candidate subsequently passes the full hosted
qualification recorded below. Other changes concern QA,
the existing workflow and SHA-derived development-version build tooling.

### Fixed diagnostic experiments, with all outcomes retained

| Source / hosted run | Original untraced workload | Traced workload | Comparison control / reintroduction |
| --- | --- | --- | --- |
| `165d47f2f53b151687f67af26f9980a648793ea1` / [36464577821](https://github.com/XNIW/Win7POS/actions/runs/36464577821) | 201.250s, 9 cycles/180 scans; raw oldest 317.672ms, warm failures 2/4/7 | 201.605s, 9 cycles/180 scans; warm cycle 3 oldest 289.593ms; trace reached its 32,768-line limit at about 148s, 10,889 drops retained | Initially unfocused TextBox plus QA 125ms timer: 181.462s, 9 cycles, oldest 1.028ms; not equivalent recurrent InputManager activity |
| `c24ac7841b4ce6804910bc5e773b207fd7ca3103` / [36466536292](https://github.com/XNIW/Win7POS/actions/runs/36466536292) | **FAIL** at Rows scan 1, Input visual probe timeout; 0 complete scans/cycles, M/E/S=false overall; recorded environment valid | 199.312s, 9 cycles/180 scans; warm cycle 8 oldest 339.984ms; **zero trace drops** | Focused TextBox plus QA 125ms timer: 181.289s, 9 cycles, oldest snapshot 18.381ms; actual InputManager timer maximum ready-to-start 32.965ms, zero trace drops |
| `a9ed5d072e11526ad7157625693be40eac5539d7` / [36468681448](https://github.com/XNIW/Win7POS/actions/runs/36468681448) | 200.338s, 9 cycles/180 scans; warm cycle 7 exceeds 250ms, raw max 256.123ms | 201.316s, 9 cycles/180 scans; warm cycle 4 oldest 357.174ms; zero trace drops | C without additional QA timer: 181.400s, oldest 10.519ms, actual InputManager max ready-to-start 34.119ms; D reintroduces only that timer: 181.368s, oldest 17.171ms, InputManager max 41.865ms. Both 9 cycles, zero drops |
| `cd396537830451e2678cd822540304074004c2c2` / [36471969876](https://github.com/XNIW/Win7POS/actions/runs/36471969876) | Not repeated for the read-only clock comparison | 198.584s, 9 cycles/180 scans; warm cycle 4 oldest 300.493ms, raw max 360.028ms, zero drops | Focused plain TextBox, 181.417s, 9 cycles, oldest snapshot 14.190ms, actual InputManager max ready-to-start 33.139ms, zero drops |
| `b512cc1924ce84bafbf386f39bb0df430c976808` / [36473824531](https://github.com/XNIW/Win7POS/actions/runs/36473824531) | 201.107s, 9 cycles/180 scans; oldest 284.814ms, warm failures 5/8 | 201.059s, 9 cycles/180 scans; oldest 326.770ms, warm failures 4/7; actual InputManager ready max 552.877ms, 161 waits over 250ms, zero drops | D restores catalog indicator constant true: 201.431s, 9 cycles/180 scans, raw oldest 362.286ms; no warm snapshot failure, but InputManager ready max 566.547ms and 108 waits over 250ms; zero drops. Catalog-indicator hypothesis disproved, no rerun to obtain another result |
| `a69b2755ca871ac78830e440fa9b5b88115a9559` / [36503934903](https://github.com/XNIW/Win7POS/actions/runs/36503934903) | **FAIL** at cold Rows scan 1, quantity applied, IsBusy=false, 0 complete scans/cycles | Fixed editor: 180.508s, 8 cycles/160 scans, oldest 20.013ms; 800 actual InputManager waits, maximum 33.260ms, none over 250ms, zero drops | D restores only editor ImageUploadProgress constant true: 201.431s, 9 cycles/180 scans, oldest 364.861ms, warm cycle 6 fails; 723 actual waits, maximum 484.189ms, 97 over 250ms, zero drops. Hidden editor animation and infinite storyboard return |

All completed Diagnostic arms report S=false by design and certify neither the
application nor Asus. A/B/C/D are serial on the same VM within each run;
runs use different VMs (EPYC 7763 or EPYC 9V74), each four logical CPUs and
the same `win25-vs2026 / 20260922.246.2` image. Cross-run timing is not a causal
before/after comparison. Hypotheses are written into artifacts before each arm.
The failed cold untraced arm is not discarded or labelled an Asus reproduction.

The runtime trace identifies the actual timer through `DispatcherOperation`
delegate target, with operation argument fields also inspected as a fallback:
**System.Windows.Input.InputManager.ValidateInputDevices**, interval **125ms**,
Background priority. IDs are ephemeral weak-key identities; only type/method
metadata and scalar timestamps are retained. Creation is explicitly recorded as
first observation, not an invented constructor timestamp. Native due offsets
are approximate modulo-2^32 deltas; acceptance ages use Stopwatch alone.

For the first traced warm failure, timer 14 was posted at 89599.837ms, its
promotion hook occurred at 89726.436ms, the snapshot at 90016.016ms and execution
started at 90049.277ms. Thus scheduled/promotion time was 126.599ms, ready wait
was 289.580ms at the snapshot and 322.841ms at start; execution took 0.015ms.
Twenty-two MediaContext Render timer callbacks started within that ready wait;
no executed callback exceeding 10ms overlapped it. The final environment sample
took 0.873ms. This separates a waiting InputManager callback from its brief
execution, but does not by itself identify the producer of repeated rendering.

The second traced run confirms the sequence with complete trace coverage:
posted 199599.807ms, promotion 199732.016ms, snapshot 200071.980ms, start
200072.382ms, end 200072.394ms. The warm ready wait remains 339.964ms before
the snapshot, followed by 0.012ms execution. The initial plain control does not
justify an environment/contract classification: investigation continues with
the active animation clocks and render state compared against the minimal control.

The third fixed experiment **disproves** the extra QA wakeup timer hypothesis:
C and D both remain below 250ms, while original A and traced B reproduce.
At B cycle 4 the InputManager timer was posted at 111967.431ms, promoted at
112093.267ms, sampled at 112450.421ms, started at 112452.453ms and ended at
112452.471ms: 125.836ms scheduled, 357.154ms ready at sampling, 359.186ms to
start, 0.018ms execution. Twenty-four MediaContext Render timer callbacks occur
during that wait, without an overlapping callback exceeding 10ms. The scene has
1,456 nodes, 13 ProgressBars with **zero** visible or hidden indeterminate bars;
only CaretElement/CaretSubElement report animated visual properties. This does
not inspect animations on brushes/resources. No hidden-animation product patch
is repeated, and no environmental exception is justified by C/D.

The next predeclared B4/C4 comparison reads existing MediaContext/TimeManager
clock metadata on the UI thread, bounded to 1,024 clocks/128 records per snapshot,
without creating a context or retaining clock/target objects. Its first dispatch
[36471405297](https://github.com/XNIW/Win7POS/actions/runs/36471405297) never reached
measurement: source `01564066369500a38545c890c7bb015332f616ca` exposed an existing
build bug, since `1.0.0-dev.015640663695` is invalid NuGet SemVer. Commit `cd39653`
prefixes entirely decimal SHA identifiers with `g`, preserves exact SHA binding,
and updates integrity/reproducibility readers. Alphabetic SHA prefixes and tagged
release versions are unchanged. Three deterministic decimal-prefix vectors pass,
as does a real locked NuGet restore of the formerly rejected version. All 49
canonical local gates pass; the existing signing fixture now covers that exact
numeric-prefix identity in hosted Security (local SignTool is unavailable). The
unexecuted dispatch is retained separately and is not a performance FAIL/PASS.

The completed B4/C4 comparison identifies a concrete difference: POS idle retains
an **active infinite Storyboard with DoubleAnimationUsingKeyFrames and
PointAnimationUsingKeyFrames at default frame rate**; the plain control has only
the caret clock at desired 10fps. At the failing warm cycle 4, the additional
storyboard is still active. InputManager was posted at 110827.751ms, promoted at
110948.989ms, sampled at 111249.447ms and started at 111312.179ms: ready age at
snapshot 300.458ms, 363.190ms to start, execution 0.009ms, 24 Render timer starts,
no overlapping executed callback over 10ms. Clock inventory is not truncated.

The [official Aero2 theme reference](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Aero2/Themes/Aero2.NormalColor.xaml)
contains that pair in the indeterminate ProgressBar storyboard. Following this
lead finds `ProductsView`'s catalog-loading indicator still set to constant
`IsIndeterminate=True`, including while the auxiliary image-list window is hidden
or closed. This is a separate indicator from the previously fixed POS/image
presenter controls. Candidate `b512cc1` binds only this indicator to effective
visibility. Its lifecycle regression exercises visible loading, completion,
restart, hidden/restored ancestor and window close. B5/D5 will compare the fix
with the exact former property value reintroduced on this control alone, using
three fixed minutes per arm and the unchanged full workload. No internal WPF
operation is removed, reprioritized or mutated; no budget is widened. B5/D5 **disproves that attribution**: the delay and the same infinite storyboard
persist with the catalog indicator corrected. The catalog change and its specific
regression were reverted; it is not presented as a performance fix.

The added window inventory provides stronger localization: immediately before
close, the catalog window has two progress controls and zero hidden indeterminate
ones; the **image editor** has one hidden indeterminate control and a hidden
animated `System.Windows.Shapes.Rectangle`. The active clock targets are
`Animation`, transform scale and RenderTransformOrigin, matching the theme
storyboard. This survives closure and is still present in the failing POS idle.
Candidate `a69b275` binds only `ProductEditDialog.ImageUploadProgress` to effective
visibility; the other behavior and the catalog source remain unchanged. Its
functional regression checks the actual editor template through idle, public
control visibility, hidden/restored ancestor and close without initiating an
upload or modifying VM internals. A6/B6/D6 repeat a fixed three minutes per arm,
with D6 restoring **only that editor control** to its old constant true state.
The completed B6/D6 comparison demonstrates an **APP lifecycle defect**: keeping
the hidden editor progress indeterminate leaves its template animation clock
active after the editor closes. D6 directly identifies the animated Rectangle's
TemplatedParent as `ProgressBar`, name `ImageUploadProgress`. Restoring that one
property restores both the extra infinite default-rate storyboard and delayed
InputManager execution; with the fix, idle retains only the normal caret clock.
The timer is the victim, not a long-running handler. At warm D6 cycle 6: posted
156535.850ms, promotion hook 156651.354ms, snapshot 157016.192ms, start
157018.412ms, end 157018.434ms. Scheduled interval is 115.504ms; ready-to-start
367.058ms; execution 0.022ms. Twenty-four MediaContext Render timer starts occur
during the ready wait, with no overlapping callback exceeding 10ms. No callback,
caret, input service or accessibility support was removed. Budget v1 is unchanged.

A6 also preserves a **distinct cold first-scan failure** before any image editor
work: expected/actual quantity 2, IsBusy=false, service 18.646ms, apply 1.266ms,
elapsed at failure 609.324ms. Pending viewport/text/render/input work is not an
executed-cause attribution. This is the 250ms visual probe, not the 10-second
command timeout, permanent deadlock or evidence explaining Asus scan 3.

### Cold fixture setup investigation

[H7 run 36505406765](https://github.com/XNIW/Win7POS/actions/runs/36505406765),
source `fb863af8436f4d8b200713249a8e4003b27d02cd`, preregistered exactly three
fresh-process five-scan trials. All complete, M/E=true, S=false (Diagnostic).
The light observer retains only the last 256 completed callbacks >=2ms, no
timer reflection/watchdog/full trace; no window is overwritten in these trials.
All three start the first scan with **zero realized fixture rows**. Executed
initial render callbacks take 146.102/143.468/140.635ms inside the first scan;
first-scan UI-return delays are 137.124/134.317/131.706ms. Parent layout validity
alone did not establish that asynchronous ItemsSource/layout had completed.
This demonstrates a harness setup boundary defect. It does not reproduce or
fully attribute the exact prior H2/A6 timeout, and those failures remain visible.

Protocol v4 checks the rendered 500-line fixture using a bounded ContextIdle
setup predicate before starting the useful clock. It records setup duration
and realized rows separately in `fixture-setup.json`; inability to become ready
within 10 seconds fails the run. No warmup scans, forced layout, discarded work,
changed public-command priority or increased 250ms probe timeout are introduced.
All subsequent scans, mode transitions, dialog/image work, idle samples and
budgets remain unchanged. The validator still evaluates historical v3 receipts
unchanged and requires the extra readiness receipt for v4. Negative vectors
reject missing/empty/legacy/timed-out setup receipts and Qualification rejects
the legacy-setup Diagnostic option.

[H8 run 36506234856](https://github.com/XNIW/Win7POS/actions/runs/36506234856),
source `07b154346ad5c362fc170b7072066dc8788df985`, preregisters exactly three
pairs, alternating legacy/ready order, identical light capture and five public
scans per process. The hypothesis is displacement of initial fixture render
into recorded setup and lower first-scan UI return; all outcomes are retained.
All six arms complete (M/E=true, S=false Diagnostic). Legacy setup has zero
realized rows in every trial; ready setup has 26 rows and takes
239.975/216.824/247.540ms, all recorded outside useful duration. First-scan
UI-return delay falls from 134.383/128.495/137.016ms to 0.228/1.976/1.087ms.
First visual probe waits are 183.281/40.749/39.311ms legacy versus
60.822/47.906/45.683ms ready: not every component improves and no timeout is
reproduced in this pair set. This confirms correct separation of setup work,
not the exact cause of historical cold or Asus timeouts. The subsequent full
hosted qualification passes independently; Asus attribution remains missing.
The executed-callback windows also localize the change: the single initial
MediaContext callback >=100ms ends 155–184ms **after** first-scan start in all
legacy arms and 76–101ms **before** it in all ready arms. All six bounded windows
have zero overwritten records; original callbacks and derived localization are
preserved together.

### Observer correction and regression evidence

The actual Framework 4.8 runtime raises `OperationPriorityChanged` **before**
publishing the new `operation.Priority`. The previous observer correctly dated
the first Inactive promotion, so this does **not** erase the historical six
failures. However, retaining the old priority could reset eligibility on a later
ready-to-ready reprioritization. The observer now retains the event timestamp,
resolves its destination on the next observation/start/hook and preserves the
age of work that was already ready. Missing or conflicting transitions invalidate
data rather than inventing an eligibility time. Finish reconciliation and
duplicate-start handling release each owned visual counter at most once.

Controlled-clock tests exercise long Inactive then fast execution, real ready
delay over 250ms, multiple priorities and Inactive re-entry, stop/restart,
aborted/completed-before-posted events, duplicate start/finish, missing finish,
missing/racing promotion, concurrent snapshots/completions and native tick wrap.
They do not sleep hundreds of milliseconds to simulate queue age. The existing
6,000 real synchronous Send completion-race regression remains active.

Canonical [CI 36466542659](https://github.com/XNIW/Win7POS/actions/runs/36466542659)
and [Security 36466542686](https://github.com/XNIW/Win7POS/actions/runs/36466542686)
both passed on `c24ac7841b4ce6804910bc5e773b207fd7ca3103`: 1,033 Core/Data tests,
zero failed/skipped; all nine functional WPF scenarios including the controlled
observer checks; canonical gates and the other existing runtime smokes. New
minimal-control options are rejected in Qualification. These observer comparisons
preserve workload protocol v3, the 250ms budget and all other acceptance predicates; observer
version 2 is recorded in new measurement receipts.
Canonical CI [36468688836](https://github.com/XNIW/Win7POS/actions/runs/36468688836)
and Security [36468688896](https://github.com/XNIW/Win7POS/actions/runs/36468688896)
also pass on `a9ed5d072e11526ad7157625693be40eac5539d7`.
CI [36473831061](https://github.com/XNIW/Win7POS/actions/runs/36473831061) and Security
[36473831125](https://github.com/XNIW/Win7POS/actions/runs/36473831125) pass on
`b512cc1`; that validates its tests, not the disproved causal hypothesis. The
`cd39653` Security run 36471976922 passed; its canonical CI 36471976935 was
cancelled by the later source push after Core/Data passed, not reported as a
complete canonical PASS.
CI [36503939032](https://github.com/XNIW/Win7POS/actions/runs/36503939032) and
Security [36503939044](https://github.com/XNIW/Win7POS/actions/runs/36503939044)
pass on `a69b275`, including the actual editor lifecycle regression. Local
protocol-v4 harness build passes with zero warnings/errors, all 49 canonical
gates pass and validator/preflight/aggregation vectors pass. Asus was not used
to execute any newly built harness.

Raw CSVs, outcomes, protocol/binary hashes, full-file manifests and selected
consecutive timer intervals are retained under [firetick evidence](evidence/2026-09-26-post-pr111/firetick/).
Full synthetic timelines remain in their workflow artifacts and the private
`C:\Dev\Win7POS-post-pr111-20260926` evidence directories; excerpts are labelled
derived and never replace the complete files. Reflection compatibility failures
are labelled unknown; no WPF internal state, priority or callback is changed.
Trace formatting/I/O is kept outside the UI-observer lock; writer shutdown drains
an already-captured batch. Startup observer-lock cost is reported separately
from the failing idle interval and is not mistaken for exclusive application CPU.
Review found a shutdown race in the diagnostic tail: a concurrent producer could
mutate the final enumerated queue. `aea6531` unsubscribes hooks, drains the writer,
copies the final batch under lock and rejects producers after observation ends.
A deterministic regression invokes a producer from another thread during the
last record's formatting and verifies the final batch/drop receipt. No acceptance
path or product behavior changes. Earlier `07b1543` canonical CI was superseded
by this correction; its partial results are not presented as a complete PASS.
Consolidated [CI 36507133349](https://github.com/XNIW/Win7POS/actions/runs/36507133349)
and [Security 36507133252](https://github.com/XNIW/Win7POS/actions/runs/36507133252)
both PASS on `aea65314b9de6f18a46a5ba9aefbeb2de363012d`, including all nine
WPF scenarios, the shutdown/setup regressions and 1,033 Core/Data tests with
zero failed/skipped. Release Pack 36508924895 is the new exact-source candidate
pack; its final verification and qualification results are recorded below.

Microsoft documents that DispatcherTimer execution depends on other queued work
and priority, with no punctual-execution guarantee. This is context, **not a
qualification waiver**. [Framework 4.8 API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer?view=netframework-4.8),
[InputManager source reference](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/InputManager.cs)
and [MediaContext source reference](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/MediaContext.cs)
were compared with observed runtime handler identities and hook order; modern
source alone is not treated as proof of the installed Framework binary.

## Consolidated hosted qualification — completed 2026-09-29 UTC, verified 2026-10-01

[Existing qualification run 36510437821](https://github.com/XNIW/Win7POS/actions/runs/36510437821)
completed both requested lanes on the unchanged source
`aea65314b9de6f18a46a5ba9aefbeb2de363012d`, after its canonical CI/Security and
[Release Pack 36508924895](https://github.com/XNIW/Win7POS/actions/runs/36508924895)
passed. The 60-minute lane started only after all three 12-minute flags passed.
Both lanes used the same GitHub Actions runner `1000004065`, Microsoft VM,
Windows Server 2025 build 26100, image `win25-vs2026/20260922.246.2`, AMD EPYC
7763 (2 cores/4 logical processors), 16GiB RAM and 96DPI. No concurrent build,
extra tracing, diagnostic ablation, forced GC or budget change was used during
measurement. The local host pause did not interrupt the cloud run.

M/E/S means MEASUREMENT_COMPLETED / ENVIRONMENT_VALID / STABILITY_PASS.
For the table, B is the unchanged budget SHA256
`63970e308d0c0ee2e2e62504c5828f5863233a74e369bc63950714787d083398`.
P12/P60 identify the actual protocol.json bytes:
`0bd744bbe6fb985342add0a914fd6ad2b56054f93f8d6632f4cc71083a50ed28` /
`87575fad6b67c3e5d37289335d50abc4afa8d9eb166dd210bb025c8d6541462b`.
They share public-scan v4 / observer v2 and protocol-text SHA256
`1c05aebd1d501e57dba87f477fbe23fc7fc3907516743040eec0f8896ccc5c64`.
Setup is recorded separately, with 26 rendered rows and 233.972/230.228ms
respectively; useful durations below exclude process startup and fixture setup.

| Lane / stage | Host / source | Useful duration / workload | Protocol / budget | M / E / S |
| --- | --- | --- | --- | --- |
| Hosted Integrated / Integrated | VM above / `aea65314b9de` | 738.848s; 33 cycles, 660 scans | v4, P12 / B | **true / true / true** |
| Hosted premerge soak / Integrated | Same VM / same `aea65314b9de` | 3601.349s; 161 cycles, 3220 scans | v4, P60 / B | **true / true / true** |
| Asus Integrated / Integrated | Asus / `aea65314b9de` prepared, not executed | NOT_EXECUTED / OWNER_DEFERRED; historical `0ed0a06` scan-3 failure remains unproven | Planned v4; no run protocol hash / B | not measured |
| Asus Final / Final | Asus / exact post-merge payload required for any future execution | NOT_EXECUTED / OWNER_DEFERRED; software closeout does not certify this lane | No run protocol hash / B | not measured |

Both hosted lanes keep 100k products/500 cart lines, all public-command scans,
Rows/Grid alternation, image/editor workload and the 20-second idle interval.
All 1,353/6,601 environment samples confirm the expected active input/thread
desktop, own foreground, visible window, AC power and no suspend/interruption.
Independent revalidation from downloaded raw CSV/receipts with the current
validator, RequiredSeconds 720/3600 and ExpectedProducts 100000 reproduces
every stored metric and all three positive flags with empty reason arrays.

| Warm metric (cycles >=2) | Hosted 12 | Hosted 60 |
| --- | --- | --- |
| Rows / Grid public UI p95 | 64.315 / 60.699ms | 60.680 / 61.150ms |
| Rows / Grid public UI maximum | 104.026 / 104.663ms | 134.683 / 133.611ms |
| Ready queue maximum / oldest eligible maximum | 1 / 9.262ms | 2 / 19.229ms |
| Private / managed idle peak | 89,874,432 / 14,138,532 bytes | 88,330,240 / 13,582,744 bytes |
| Maximum five-cycle managed mean growth | 0 bytes | 364,053.6 bytes |
| Grid containers / metadata entries / decoded cache | 12 / 500 / 0 bytes | 12 / 500 / 0 bytes |
| Closed-window observed roots / observer drops | 0 / 0 | 0 / 0 |

The six historical v3 failures remain unchanged. H6 supplies the causal
before/after test for the editor animation; this full v4 run establishes
candidate qualification after the separately proven setup correction. It does
not identify the cause of the distinct earlier Asus timeout or certify Windows 7.

Release binding was independently cross-checked against all 41 downloaded
payload files and both lanes' identical binary inventories. Hosted harness
SHA256 is `d970d11b5910efb793439f2a6b26ef99355834d0d5b747af837da571a9b54676`;
the locally compiled, unexecuted Asus harness has the separate hash in the owner
card. Setup SHA256 is
`290dc8abae7bfbcc28d57bfed5cba116a23bd408bb2f1b2e48b431d23a443ca1`;
payload ZIP SHA256 is
`8e6f1c0065bcdf94e29522d725561162924a19557c5133adb8c77c33ab7bc7e8`.
The pack is **development-unsigned**, not a production-signed release.
Archive digests, signature states, raw run/host/binding/setup receipts, CSVs and
independent calculations are retained in the
[verified summary](evidence/2026-09-26-post-pr111/firetick/36510437821/verified-summary.json)
and its 38-file raw-byte manifest. Qualification artifact SHA256 is
`1f090c1df81602eb3c0b133be80c4ab1422251ef59a2bf68e77a601bd3ab227b`.
Downloaded installer integrity passes; install/upgrade/uninstall, physical
Win7/peripherals, staging and image recovery remain **NOT_EXECUTED**.

Publication after this measured source changes only documentation/evidence.
Its head and exact checks are recorded in the external interim attestation,
without rebinding these measurements to newly built documentation-head bytes.
PR112 remains OPEN/DRAFT; `origin/main` is still
`bedcf17a97d814a3b098387cc2720ff2b11abbbf`. No merge or Final is claimed.

## Historical full hosted Integrated qualification — 2026-09-28

Resumed from `8111c8f9357d09a68343613080bf0b47041efc09`. The only new code is
the existing CI workflow's explicit hosted qualification path and seven tests
of its failure aggregation. Application code, public-scan protocol v3 and
frozen budget bytes are unchanged. Qualification has no trace, short-scan,
Input-dispatch or observer-disable option. Manual runs have their own concurrency
identity and cannot be cancelled by a later documentation PR update. The manual
job does not replace the canonical PR checks.

The [canonical Release Pack 36454068992](https://github.com/XNIW/Win7POS/actions/runs/36454068992)
passed on `cb43fd4b23cd26859ab2f370d1c0cfe47aaff205`, version
`1.0.0-dev.cb43fd4b23cd`. The existing downloaded-pack verifier checked all three
GitHub archive digests, integrity/provenance, matching dist/ZIP/installer and
41 payload files. The existing payload module then overlaid those actual bytes
on the exact-source x86 harness. No synthetic replacement manifest was used.
The SDK was 10.0.301 with locked restore. The separate
[CI 36454075173](https://github.com/XNIW/Win7POS/actions/runs/36454075173) and
[Security 36454075193](https://github.com/XNIW/Win7POS/actions/runs/36454075193)
also passed on this code revision: 49 gates, 1,033 Core/Data tests with zero
failed/skipped, all nine WPF functional scenarios and the other canonical smokes.
The 32 validator vectors and six runner preflights remain active, including
the individual `focus_scroll_wait_ms` check; seven new workflow aggregation
cases reject failed/skipped measurements and missing/nonpositive receipts.

[Qualification run 36456455827](https://github.com/XNIW/Win7POS/actions/runs/36456455827)
completed **733.084 useful seconds, 33 cycles, 660 public scans** with 100k
products and 500 persistent cart identities. All 1,353 environment samples were
valid. Host: Microsoft VM, AMD EPYC 7763, four logical CPUs, approximately 16GiB,
Windows Server 2025 build 26100, image `win25-vs2026 / 20260922.246.2`, 96 DPI.
Runtime: x86, CLR 4.0.30319.42000, Framework 4.8.9345.0, WPF 4.8.9347.0.
This host is distinct from Asus; no cross-host performance improvement is inferred.

| Lane | Duration / host / source | M / E / S |
| --- | --- | --- |
| QUALIFICA_HOSTED, Integrated | 733.084s; VM above; `cb43fd4b23cd` | true / true / **false** |
| PREMERGE_HOSTED_SOAK, Integrated | Not started: 12-minute validator failed; same candidate was requested | not measured |
| Asus Integrated | Historical `0ed0a061f5d8`; interrupted at Rows scan 3 before a complete cycle | false / false / false overall; early environment samples valid |
| Asus Final | Not started; no merge or exact-merge payload exists | not measured |

Hosted harness SHA256:
`787e3262a9e68609c8929333589a78d48828a0b1fb8bc33270cc1752d31ef4ab`.
Executed WPF SHA256:
`13526398aa361326920c27c63e60b10a8c3f93f239a876aeda60cfc5f3e540b3`.
Budget SHA256 remains
`63970e308d0c0ee2e2e62504c5828f5863233a74e369bc63950714787d083398`.
The historical Asus harness/WPF hashes remain in the unchanged interactive
attempt evidence and external attestation; they are not this hosted payload.

The **first specific failure** is `dispatcher_backlog:cycle=3`, at 89.661 useful
seconds: three pending operations, oldest eligible age **280.275ms**, over the
250ms limit. The operation snapshot identifies `Background:FireTick:280.3`,
plus Input marker and Render timer work. The same reason occurs at cycles
12, 13, 17, 18 and 23; maximum age is **309.130ms** at cycle 13. Pending count
never exceeds three, so the failing condition is age, not queue size.
Own focus/scroll are zero after idle; no closed-window roots or observer drops
are observed. Subsequent Background probes are at most 0.696ms and Input probes
at most 74.466ms: these later probes do not erase the earlier queue-age failure.

This evidence identifies a pending timer callback, **not its owner or the work
that delayed it**, and does not establish an application defect, an observer
defect or the cause of the different Asus third-scan timeout. No callback was
excluded and no threshold, dispatch priority or application code was changed
to turn this run green. Before a new hosted experiment, the discriminating
hypothesis is that the eligible timer is deferred by actual dispatcher/native
input scheduling during idle. A bounded trace must identify its owner and
promotion/start timestamps and the operation executing during that interval;
only demonstrated observation accounting or application scheduling errors
justify a patch. A repeated unmodified five-scan run cannot test this idle failure.

Warm statistics use the original two-cycle warm-up, with every raw sample retained:

| Full public UI milliseconds | p50 | p95 | max | Scan 2 p50 / p95 / max | Scan 3 p50 / p95 / max |
| --- | ---: | ---: | ---: | --- | --- |
| Rows, 310 warm scans | 40.518 | 68.496 | 98.949 | 38.561 / 55.503 / 58.679 | 39.899 / 52.897 / 58.613 |
| Grid, 310 warm scans | 43.407 | 61.105 | 98.935 | 40.355 / 52.357 / 61.105 | 40.720 / 52.524 / 55.598 |

Cold Rows scans 1/2/3 are 353.087 / 37.334 / 49.027ms, retained in full.
Isolated service p95 is 12.828ms; Send probe p95 0.093ms. Full UI includes
command overhead and visual completion; bitmap cost remains separate. Warm
private/managed peaks are 87.020/16.326MiB, managed rolling-window growth zero,
ready queue 0–3, Grid containers 12, metadata entries 500, decoded cache pixels
zero, collection changes zero over all 660 scans. Individual visual wait peaks
at 69.534ms. These positive submetrics do not override the six queue-age failures.
The comparable historical before/after reset-stress results below retain their
original host/protocol; they are not compared numerically with this new VM run.

The job stayed **FAIL** after evidence collection; the 60-minute step was skipped.
Local revalidation of the downloaded CSVs reproduces exactly M=true/E=true/S=false
and the same six reasons. [Summary, hashes and full synthetic evidence](evidence/2026-09-26-post-pr111/hosted-qualification-summary.json)
include protocol, binding, canonical download receipt, runtime and all CSVs.
No executables, databases, images, checkpoint, keys or private dumps are published.
The development-unsigned package/installer were built and verified, not installed
or certified on Win7. The 55 unrelated worktrees and DPAPI checkpoint are unchanged.
The single targeted prerequisite check found SAC still On, no new repository
signing configuration, and Admin main still `fe4907adc51ff842720e1c7eb36aa05e0fa53cb8`;
no staging cleanup, hardware test or denied local executable retry was attempted.

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
| HOSTED-TIMER-AGE (RESOLVED ON HOSTED QA) | Actual 125ms Background InputManager.ValidateInputDevices waits behind recurring rendering from hidden ProductEditDialog.ImageUploadProgress. Same-host H6 B: 800 waits, max 33.260ms, none >250; D restores only old indeterminate value: 97/723 waits >250, warm snapshot 364.861ms, hidden animation and infinite clock return. | Bind that editor indicator to effective IsVisible; actual lifecycle regression PASS. Consolidated aea6531 CI/Security/Release Pack PASS; full 12+60 hosted qualification M/E/S=true. Historical six v1-budget failures retained. |
| HOSTED-COLD-SETUP (HARNESS CORRECTION VERIFIED; EXACT OLD TIMEOUT UNATTRIBUTED) | H7 all three first scans start with zero fixture containers and execute initial render 141–146ms inside scan timing. H8 pairs confirm first UI-return 128–137ms legacy versus 0.2–2.0ms after readiness. Exact preceding A6 timeout cause remains unproven. | v4 records bounded rendered-fixture setup before useful clock, no warmup scans or changed latency budgets. All six H8 arms complete; consolidated full hosted 12+60 subsequently passes. |
| ASUS-THIRD-SCAN (OWNER_ACCEPTED_DEFERRED_EXTERNAL_QA) | Earlier cycle-0 scan-3 Input-probe timeout remains distinct from later SAC denial. The preserved channel check found SAC On, unsigned candidate and no configured trusted channel. Root cause remains unproven. | Owner explicitly defers this external Asus QA on 2026-10-01. No unsigned retry, policy change or false PASS. Any future reproduction still requires an authorized accepted candidate. |
| PERF-LATENCY | Resetting 500 identities each cycle repeatedly realizes all 500 hidden Grid cards. Removing only the hidden Grid ItemsSource eliminates multi-second Rows stalls; restoring it reproduces them. A visibility guard alone does not. Ordinary scans have zero collection changes. | Replace eager WrapPanel with a recycling, variable-height, multi-column `VirtualizingCartWrapPanel`. Preserve card width, selection, scrolling and resize. Runtime regression checks attached visible containers at indices 0/250/499, four widths, both views, removal/empty/re-add and public commands. Corrected an initially detached recycled-container implementation before accepting measurements. |
| PERF-QUEUE | 501 invisible ProgressBars remain indeterminate. Original mixed control leaves 96 operations, oldest 38,262ms after four cycles; disabling only invisible indicators leaves two, oldest 18ms. Reintroduction reproduces accumulation. Producer stacks identify TextEditor.OnTextViewUpdated during arrange. | Bind IsIndeterminate to actual IsVisible in the cart busy indicator and ProductImagePresenter. Test loading-visible, ancestor-hidden and completed-image states. Do not abort, reprioritize or remove internal WPF operations. |
| PERF-MEMORY | Pending Background InitTextStore delegates retain TextEditor._uiScope → TextBox → dialog parents → closed ProductEditDialog → VM. Animated rendering repeatedly allocates while the controls are invisible; removing that work drains the demonstrated root chain. Eager card trees also retain 500 control subtrees. | Same two application fixes. Observe natural GC, post-idle heap/private bytes, known closed-window root chains and decoded cache pixel bytes. No forced GC. This establishes those paths, not an exhaustive heap census. |
| PERF-VALIDATION | Previous runner verified process/sample/duration completion but no stability predicate. Legacy observer scans a weak-reference list on completion; replacement has fixed capacity and O(1) normal completion. Replacing constructor VM disconnects real focus wiring. Reset stress and chained high-priority continuations are harness artifacts. | Preserve real PosView composition, persistent identities, alternating mode order, explicit Input completion inside scan timing, separate bitmap timing, bounded observer and three independent result flags. Synthetic negative vectors cover stalls, partial recovery, invalid/missing data, environment, timeouts, roots, overflow and visual waits. |
| INTERACTIVE-RESIDUAL (OWNER_DEFERRED_EXTERNAL_QA) | Asus cycle 0 / Rows scan 3 times out even without the observer, with valid foreground/desktop samples. Pending UpdatePeer callbacks do not identify the executed blocker. Hosted controls do not reproduce it. | No residual production fix claimed. Asus attribution and equivalent before/after remain missing; owner accepts deferral for software closeout, not a measured PASS. |
| QA-EXECUTION (OWNER_DEFERRED_EXTERNAL_QA) | SAC rejects v17 before process creation; separate from the earlier running-process timeout. Existing signing assessment finds no configured trusted channel. | Hosted builds remain in their independent QA channel. Owner permits software merge based on exact-HEAD gates while deferring Asus acceptance; no execution/policy bypass or physical certification. |

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
ProductImagePresenter and ProductEditDialog (invisible animations), and opt-in stage instrumentation
in implicated AddByBarcode/BuildSnapshot/ApplySnapshot methods. The measurement
scope has no SQL text, barcode logging or production I/O and is inactive normally.
The test helpers separate factorial reproductions, functional regression,
environment and bounded observation from qualification orchestration.
The runner, validator and canonical gate connect these checks to existing CI;
no long soak is added to every commit.

Initial local validation: serial locked restore, 49/49 gates, solution/WPF/harness builds, 1,033/1,033
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
Asus attempt failed; current hosted Integrated and premerge soak subsequently
pass as recorded above. Asus Integrated and downloaded merge-package Final
remain pending. No final performance PASS is claimed.

Owner-approved sequence on 2026-10-01: audit only PR112's delta, reuse unchanged
software evidence, record external QA deferral, require CI/Security PASS on the
exact final PR HEAD, merge normally, then download and canonically verify the
Release Pack for that exact merge SHA. No redundant long qualification is
required for this documentation-only closeout. Production certification remains
false. Any future Asus diagnostic/Integrated/Final follows the preserved owner
activation protocol and unchanged budgets; it is not executed by this closeout.

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
| Asus QA executable acceptance / PC owner and authorized signing administrator | Make the existing authorized trusted code-signing channel available for the compiled candidate below, following [Microsoft's signing procedure](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control). Record hashes before/after, verify Authenticode and chain, then bind the accepted bytes. No certificate purchase, self-signed trust, policy change or hosted-binary substitution. | Accepted exact-source harness, short untraced/traced Asus reproduction; no retry of the denied v17 file. |
| Article staging and image recovery / Admin deployment and shop owner | Provide current typed readiness plus authorized recovery/cleanup handoff. No new readiness or cleanup is invented. Existing DPAPI checkpoint and `cleanupPending` remain unchanged. | Canonical staging/image runner only after local performance is stabilized. |
| Installer and Windows 7 / QA host and hardware owner | Provide disposable VM/snapshot for install/upgrade/uninstall and an authorized Win7 SP1 net48/x86 target. Scanner/IME and printer/drawer need their real hardware/driver checks and operational consent. | Exact verified merge package, economic/offline/restart/backup checks. Hosted Windows QA does not certify Win7. |

These rows are preserved prerequisites for future owner-authorized external QA,
all now owner-deferred; none is an immediate activation or software-merge gate.
The locally compiled candidate has
source `aea65314b9de6f18a46a5ba9aefbeb2de363012d` and has **not been executed**.
File: `C:\Dev\Win7POS\tests\Win7POS.Wpf.UiSmokeHarness\bin\x86\Release\net48\Win7POS.Wpf.UiSmokeHarness.exe`.
Unsigned SHA256: `ddb03639b9c3f2f3ddc60d00096d597ab0e58689b4dcf176f2498f846462c951`.
Its 33 exe/DLL hashes and signature states are recorded privately in
`C:\Dev\Win7POS-post-pr111-20260926\owner-activation-aea6531.json`;
the single operational card is `ASUS-OWNER-ACTIVATION.md` in that same directory.
This is a signing/acceptance handoff, not authorization to retry an unsigned file.

The following activation commands are retained for future owner-authorized Asus
QA only. They are not executed by this software closeout and are not its merge
gate. After owner-channel acceptance and signature/chain verification, resume
from the repository root with the existing runner and a new output directory:

```powershell
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -Mode Diagnostic -DiagnosticScanCount 5 -Products 100000 -HarnessDirectory 'C:\Dev\Win7POS\tests\Win7POS.Wpf.UiSmokeHarness\bin\x86\Release\net48' -OutputDirectory ('C:\Dev\Win7POS-post-pr111-20260926\asus-accepted-aea6531-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
if ($LASTEXITCODE -ne 0) { throw 'Preserve the Asus failure and identify its first cause.' }
```

Verify five quantity increments, valid desktop/foreground, zero trace loss and
actual executed-operation attribution. A failure returns to that short
reproducer. Only after a demonstrated correction and the functional suite pass,
resolve `$HarnessDir`, `$Binding` and `$HeadSha` from a newly accepted,
canonically verified candidate of that exact revision, and new evidence paths.
Do not reuse the old `0ed0a06` binding or its head-bound resume script. Use the
unchanged budget hash stated above:

```powershell
pwsh -NoProfile -File scripts/run-cart-performance.ps1 -Mode Qualification -Stage Integrated -Products 100000 -SoakMinutes 12 -HarnessDirectory $HarnessDir -BudgetPath $Budget -PayloadBindingPath $Binding -ExpectedCommit $HeadSha -OutputDirectory $IntegratedOut
```

Any future Asus physical qualification requires all three flags true, the
properly bound candidate and, for Final, the exact merge package with at least
60 useful minutes. Current hosted Integrated and premerge soak both pass;
Asus Integrated/Final remain NOT_EXECUTED / OWNER_DEFERRED. Under the explicit
2026-10-01 owner decision, PR112 may leave draft and merge normally after
exact-final-HEAD CI/Security PASS and delta audit, with productionCertified=false.
