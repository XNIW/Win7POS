"""Summarize synthetic benchmark CSVs without excluding outliers (Python 3)."""
import argparse
import csv
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


def read_csv(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def stats(values):
    values = sorted(float(value) for value in values)
    if not values:
        return None
    if any(not math.isfinite(value) for value in values):
        raise ValueError("Non-finite measurement")
    return dict(n=len(values), min=values[0],
                p50=values[math.ceil(len(values) * .5) - 1],
                p95=values[math.ceil(len(values) * .95) - 1],
                max=values[-1], mean=statistics.mean(values))


def benchmark(directory):
    output = []

    def append(operation, records, field):
        first = [row for row in records if int(row["sample"]) == 0]
        warm = [row for row in records if int(row["sample"]) > 0]
        if len(first) != 1 or sorted(int(row["sample"]) for row in warm) != list(range(1, 31)):
            raise ValueError(f"Incomplete or duplicated benchmark: {directory.name}/{operation}")
        summary = stats(row[field] for row in warm)
        output.append(dict(dataset=directory.name, operation=operation,
                           first_ms=float(first[0][field]), warm_n=summary["n"],
                           warm_p50_ms=summary["p50"], warm_p95_ms=summary["p95"],
                           warm_max_ms=summary["max"]))

    for filename, fields in (("cart-performance.csv", ("cart", "operation")),
                             ("flow-performance.csv", ("operation",))):
        groups = defaultdict(list)
        for row in read_csv(directory / filename):
            groups[", ".join(row[field] for field in fields)].append(row)
        for operation, records in groups.items():
            # First view entry is intentionally a single observation, not 30 warm trials.
            if (filename == "flow-performance.csv" and operation == "pos_view_entry_first_in_process"
                    and len(records) == 1 and int(records[0]["sample"]) == 0):
                output.append(dict(dataset=directory.name, operation=operation,
                                   first_ms=float(records[0]["ms"]), warm_n=0,
                                   warm_p50_ms="", warm_p95_ms="", warm_max_ms=""))
            else:
                append(operation, records, "ms")
    stages = read_csv(directory / "render-stages.csv")
    for field in ("service_ms", "apply_ms", "layout_ms", "bitmap_allocate_ms", "bitmap_render_ms"):
        append(field, stages, field)
    return output


def soak(directory):
    rows = read_csv(directory / "cart-soak.csv")
    idle = [row for row in rows if row["phase"] == "after_idle"]
    if not rows or not idle:
        raise ValueError("No completed idle intervals")
    elapsed = [float(row["elapsed_s"]) for row in rows]
    if elapsed != sorted(elapsed):
        raise ValueError("Elapsed time must be monotonic")
    if len({row["products"] for row in rows}) != 1:
        raise ValueError("Mixed product datasets")
    result = dict(dataset=directory.name, samples=len(rows), cycles=len(idle),
                  elapsedSeconds=elapsed[-1], firstIdle=idle[0], lastIdle=idle[-1],
                  all={}, idle={}, scans={}, tenCycleWindows=[])
    cycle_ends = [float(row["elapsed_s"]) for row in idle]
    result["completeCycleSeconds"] = dict(first=cycle_ends[0],
        subsequent=stats(end - previous for previous, end in zip(cycle_ends, cycle_ends[1:])),
        note="Includes the 20-second idle interval; first cycle also includes initial host setup.")
    fields = ("private_bytes", "managed_bytes", "handles", "gdi", "user", "threads",
              "dispatcher_ms", "pending_dispatcher", "cache_entries", "image_lookups",
              "gc0", "gc1", "gc2")
    if "native_input_pending" in rows[0]:
        fields += ("native_input_pending",)
    for field in fields:
        result["all"][field] = stats(row[field] for row in rows)
        result["idle"][field] = dict(stats=stats(row[field] for row in idle),
            first10Mean=stats(row[field] for row in idle[:10])["mean"],
            last10Mean=stats(row[field] for row in idle[-10:])["mean"])
    for phase in ("Rows", "Grid"):
        phase_rows = [row for row in rows if row["phase"] == phase]
        result["scans"][phase] = {field: stats(row[field] for row in phase_rows)
                                  for field in ("scan_ms", "render_ms")}
        cycles = defaultdict(list)
        for row in phase_rows:
            cycles[row["cycle"]].append(row)
        result["scans"][phase]["modeEntryScan"] = stats(records[0]["scan_ms"] for records in cycles.values())
        result["scans"][phase]["laterScans"] = stats(row["scan_ms"] for records in cycles.values() for row in records[1:])
        result["scans"][phase]["samplesPerCycle"] = {cycle: len(records) for cycle, records in cycles.items()}
        result["scans"][phase]["byScanOrdinal"] = {
            str(index + 1): stats(records[index]["scan_ms"] for records in cycles.values() if len(records) > index)
            for index in range(max((len(records) for records in cycles.values()), default=0))}
    after = {row["cycle"]: row for row in idle}
    deltas = [float(after[row["cycle"]]["private_bytes"]) - float(row["private_bytes"])
              for row in rows if row["phase"] == "before_idle" and row["cycle"] in after]
    result["idleRecovery"] = dict(privateDeltaBytes=stats(deltas),
                                  negativeDeltas=sum(value < 0 for value in deltas))
    for offset in range(0, len(idle), 10):
        window = idle[offset:offset + 10]
        result["tenCycleWindows"].append(dict(firstCycle=int(window[0]["cycle"]),
            lastCycle=int(window[-1]["cycle"]), n=len(window),
            **{field: stats(row[field] for row in window)
               for field in ("private_bytes", "managed_bytes", "pending_dispatcher")}))
    return result


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--benchmark", type=Path, action="append", required=True)
parser.add_argument("--soak", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True, help="New output directory")
args = parser.parse_args()
benchmark_rows = [row for directory in args.benchmark for row in benchmark(directory)]
soak_result = soak(args.soak)
args.output.mkdir(parents=True, exist_ok=False)
with (args.output / "benchmark-summary.csv").open("w", encoding="utf-8", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=list(benchmark_rows[0]))
    writer.writeheader()
    writer.writerows(benchmark_rows)
(args.output / "soak-summary.json").write_text(json.dumps(soak_result, indent=2) + "\n", encoding="utf-8")
print(f"SUMMARY_WRITTEN={args.output}; this script summarizes samples, not qualification status")
