# Post-PR111 root cause optimization — ASUS-W7POS-018

Baseline: `bedcf17a97d814a3b098387cc2720ff2b11abbbf` (PR109–111).
Status: implementation and short causal verification in progress; **not yet
qualified**. The historical b794 soak remains STABILITY=FAIL. Final merge,
package binding and external attestation are pending.

## Causal ledger

| Finding | Supported cause and discriminating evidence | Change and regression |
| --- | --- | --- |
| PERF-LATENCY | Resetting 500 identities each cycle repeatedly realizes all 500 hidden Grid cards. Removing only the hidden Grid ItemsSource eliminates multi-second Rows stalls; restoring it reproduces them. A visibility guard alone does not. Ordinary scans have zero collection changes. | Replace eager WrapPanel with a recycling, variable-height, multi-column `VirtualizingCartWrapPanel`. Preserve card width, selection, scrolling and resize. Runtime regression checks attached visible containers at indices 0/250/499, four widths, both views, removal/empty/re-add and public commands. Corrected an initially detached recycled-container implementation before accepting measurements. |
| PERF-QUEUE | 501 invisible ProgressBars remain indeterminate. Original mixed control leaves 96 operations, oldest 38,262ms after four cycles; disabling only invisible indicators leaves two, oldest 18ms. Reintroduction reproduces accumulation. Producer stacks identify TextEditor.OnTextViewUpdated during arrange. | Bind IsIndeterminate to actual IsVisible in the cart busy indicator and ProductImagePresenter. Test loading-visible, ancestor-hidden and completed-image states. Do not abort, reprioritize or remove internal WPF operations. |
| PERF-MEMORY | Pending Background InitTextStore delegates retain TextEditor._uiScope → TextBox → dialog parents → closed ProductEditDialog → VM. Animated rendering repeatedly allocates while the controls are invisible; removing that work drains the demonstrated root chain. Eager card trees also retain 500 control subtrees. | Same two application fixes. Observe natural GC, post-idle heap/private bytes, known closed-window root chains and decoded cache pixel bytes. No forced GC. This establishes those paths, not an exhaustive heap census. |
| PERF-VALIDATION | Previous runner verified process/sample/duration completion but no stability predicate. Legacy observer scans a weak-reference list on completion; replacement has fixed capacity and O(1) normal completion. Replacing constructor VM disconnects real focus wiring. Reset stress and chained Normal continuations are harness artifacts. | Preserve real PosView composition, persistent identities, alternating mode order, explicit Input completion inside scan timing, separate bitmap timing, bounded observer and three independent result flags. Synthetic negative vectors cover stalls, partial recovery, invalid/missing data, environment, timeouts, roots, overflow and visual waits. |

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
- An uninterrupted chain of Normal continuations postponed focus/scroll through
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

| Products / rows | Rows p95 before → patched | Rows maximum before → patched | Scan 2 median before → patched | Scan 3 median before → patched |
| --- | --- | --- | --- | --- |
| 20k / 500 | 3422.55 → 192.26ms | 3422.59 → 245.38ms | 3422.55 → 12.81ms | 17.24 → 8.12ms |
| 100k / 500 | 3394.40 → 186.64ms | 3693.49 → 192.82ms | 3106.70 → 12.19ms | 33.14 → 13.02ms |

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

The latest structural regression passed public remove/re-add, empty-grid and
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
Current host inventory is Windows 11 Home Single Language 10.0.26200 x64;
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
product paging also pass. The validator has 26 passing positive/negative vectors,
including wrong dataset/cart receipts and the old soak rejection. PR/head CI,
valid integrated qualification and downloaded final-package qualification remain
pending. No final performance PASS is claimed.

Next authorized sequence: provide an active/unlocked QA desktop, rerun the
interactive regression and a 10–15 minute Integrated qualification against the
unchanged preregistered budget; then exact-head CI/normal merge, download and
validate the merge Release Pack, repeat 20k/100k measurements and run at least
60 useful minutes of Final qualification. The unavailable desktop is not an
accepted exception and no final soak is started while its validity is known false.

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
