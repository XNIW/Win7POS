# Post-PR109 synthetic measurements

All CSV samples are retained, including outliers. Times are milliseconds;
memory fields are bytes, cumulative GC counts are collection counts. No
forced collection was used. Sample 0 is first invocation in the same process,
not process startup. For samples 1..30, sort ascending and select one-based
rank `ceil(p*n)` for p50/p95; maximum includes every warm sample.

| Directory | Status and use |
| --- | --- |
| measured-20000 | Accepted 20k benchmark, downloaded PR109 runtime |
| final-soak-100000 | Completed 100k benchmark; long soak INTERRUPTED by lid-triggered Modern Standby after 108 cycles and 3507.805 seconds of samples; not a 60-minute PASS |
| completed-soak-100000-20260926 | Historical directory name only: FAILED after three cycles and a partial fourth with ProductEditDialog disposed-source exception; not completed |
| probe-20000 | Stopped Background-priority observation; not qualification |
| probe-v2-20000 | Completed short Send-priority probe, strong-reference observer; not long-run qualification |
| soak-100000 | Stopped first long probe with strong-reference observer; invalid as retention qualification |
| qualified-100000 | Historical folder name only: stopped weak-reference probe before full duration; not accepted qualification |
| queue-diagnostic | Completed one-minute local-build diagnostic probe |
| diagnostic-package-100k | Completed one-minute downloaded-runtime diagnostic probe |

Each available `protocol.json` lists the precise EXE/DLL hashes at invocation.
The diagnostic long run uses weak references to observe dispatcher operations,
prunes completed/aborted operations, and logs callback classes separately in
private evidence. Image tests repeatedly construct real WPF presenters/editor
using synthetic image states; they do not qualify remote transfers or a real
photo decode/cache endurance matrix. Synthetic sales in the benchmark are
local fixtures with isolated databases and no remote requests.

Reproduce from a Release net48/x86 harness using
`scripts/run-cart-performance.ps1`, specifying a new output directory, either
`-Products 20000` or `-Products 100000`, and `-SoakMinutes 60` for the long run.
Use `-HarnessDirectory` for a harness overlaid with a downloaded payload and
retain its manifest. Do not run builds or other local tests concurrently.
Keep the QA host awake with its lid open for the requested duration; host
suspension does not count as executed workload and the timeout remains active.
See the [main report](../../2026-09-25_POST_PR109_RESIDUAL_CLOSEOUT.md) for
environment, stage interpretation, measurement binding and external limits.
