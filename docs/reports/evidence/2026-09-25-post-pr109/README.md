# Post-PR109 synthetic measurements

All CSV samples are retained, including outliers. Times are milliseconds;
memory fields are bytes, cumulative GC counts are collection counts. No
forced collection was used. Sample 0 is first invocation in the same process,
not process startup. For samples 1..30, sort ascending and select one-based
rank `ceil(p*n)` for p50/p95; maximum includes every warm sample.
Cart sizes 1/10/50/100/500 run in sequence in the same process. "First" is
case-relative; later cases have already executed the preceding workloads.

| Directory | Status and use |
| --- | --- |
| measured-20000 | Accepted 20k benchmark, downloaded PR109 runtime |
| final-soak-100000 | Completed 100k benchmark; long soak INTERRUPTED by lid-triggered Modern Standby after 108 cycles and 3507.805 seconds of samples; not a 60-minute PASS |
| completed-soak-100000-20260926 | Historical directory name only: FAILED after three cycles and a partial fourth with ProductEditDialog disposed-source exception; not completed |
| patched-local-probe-100000-20260926 | Completed local patched-build probe, 322.783 seconds / 11 cycles; no crash, but background queue grows to 235; not a memory-stability PASS |
| interrupted-patched-soak-100000 | Patched downloaded runtime; interrupted during Codex sandbox service restart after 107 complete cycles / 3299.835 seconds; no terminal PASS; all 41 payload hashes rechecked unchanged |
| patched-package-benchmark-20000 | Completed 20k benchmark on downloaded b79451f0a475 runtime; first + 30 warm samples |
| patched-package-soak-100000 | Completed 100k benchmark and 60m17s soak, 115 cycles / 2,530 samples; execution PASS, memory/queue and UI latency stability qualification FAIL; all 41 payload hashes unchanged |
| patched-summary | Reproducible CSV/JSON statistics and complete soak chart; no samples removed |
| probe-20000 | Stopped Background-priority observation; not qualification |
| probe-v2-20000 | Completed short Send-priority probe, strong-reference observer; not long-run qualification |
| soak-100000 | Stopped first long probe with strong-reference observer; invalid as retention qualification |
| qualified-100000 | Historical folder name only: stopped weak-reference probe before full duration; not accepted qualification |
| queue-diagnostic | Completed one-minute local-build diagnostic probe |
| diagnostic-package-100k | Completed one-minute downloaded-runtime diagnostic probe |

Each available `protocol.json` lists the precise EXE/DLL hashes at invocation.
The diagnostic long run uses weak references to observe dispatcher operations,
prunes completed/aborted operations, and records callback classes in the
included `dispatcher-operations.txt` files where available. Image tests repeatedly construct real WPF presenters/editor
using synthetic image states; they do not qualify remote transfers or a real
photo decode/cache endurance matrix. Synthetic sales in the benchmark are
local fixtures with isolated databases and no remote requests.

The patched probe adds `native_input_pending`: WPF's optional native pending
check (`-1` if unavailable). This flag includes native system events and is
not proof of keyboard/scanner activity. Dispatcher latency is measured at
Send priority and does not imply that lower-priority queues have drained.

Reproduce from a Release net48/x86 harness using
`scripts/run-cart-performance.ps1`, specifying a new output directory, either
`-Products 20000` or `-Products 100000`, and `-SoakMinutes 60` for the long run.
Use `-HarnessDirectory` for a harness overlaid with a downloaded payload and
retain its manifest. Do not run builds or other local tests concurrently.
Keep the QA host awake with its lid open for the requested duration; host
suspension does not count as executed workload and the timeout remains active.
See the [main report](../../2026-09-25_POST_PR109_RESIDUAL_CLOSEOUT.md) for
environment, stage interpretation, measurement binding and external limits.

## Reproduce the summaries and chart

`summarize-performance.py` uses only Python 3's standard library. It rejects
missing or duplicated warm samples, keeps the single first-view observation
separate, and reports nearest-rank percentiles, first/last ten equivalent idle
cycles, all peaks and per-cycle idle recovery. A generated summary is not a
qualification PASS; completion and binary identity require the runner receipt.
`plot-soak.py` additionally requires Matplotlib. `requirements-plot.txt` pins
the actual Python 3.12 chart environment; install it in a separate virtual
environment (`python -m venv C:\QA\post109-chart-env`, then that environment's
`python -m pip install -r requirements-plot.txt`). Both scripts operate only on CSVs.
From this evidence directory, after obtaining the two recorded datasets:

```powershell
python summarize-performance.py --benchmark patched-package-benchmark-20000 --benchmark patched-package-soak-100000 --soak patched-package-soak-100000 --output C:\QA\post109-summary
python plot-soak.py patched-package-soak-100000/cart-soak.csv C:\QA\post109-summary/soak.png
```

Use a new summary output directory. The chart includes all private-memory and
queue samples with equivalent after-idle measurements overlaid; it starts both
vertical axes at zero. No smoothing, outlier removal or forced collection is
applied. Send-priority probe latency is not a responsiveness bound for normal
input or lower-priority background callbacks.
