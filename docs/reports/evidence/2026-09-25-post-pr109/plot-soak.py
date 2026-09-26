"""Plot recorded soak samples; requires Python 3 and matplotlib on the QA host."""
import argparse
import csv
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("csv", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
with args.csv.open(encoding="utf-8-sig", newline="") as stream:
    rows = list(csv.DictReader(stream))
if not rows:
    raise ValueError("No recorded samples")
idle = [row for row in rows if row["phase"] == "after_idle"]
if not idle:
    raise ValueError("No completed idle intervals")
products = {int(row["products"]) for row in rows}
if len(products) != 1:
    raise ValueError("Do not combine different datasets into one time series")

def values(records, field, divisor=1):
    result = [float(row[field]) / divisor for row in records]
    if any(not math.isfinite(value) or value < 0 for value in result):
        raise ValueError("Invalid observed value: " + field)
    return result

x = values(rows, "elapsed_s", 60)
xi = values(idle, "elapsed_s", 60)
if x != sorted(x):
    raise ValueError("Elapsed time must be monotonic")
plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 11})
fig, axes = plt.subplots(2, 1, figsize=(12, 8), sharex=True)
blue, orange, gray = "#245B91", "#B36327", "#B7BBC1"
axes[0].plot(x, values(rows, "private_bytes", 1024**2), color=gray,
             linewidth=.7, label="Private memory: all samples")
axes[0].plot(xi, values(idle, "private_bytes", 1024**2), color=blue,
             linewidth=1.6, label="Private memory: after 20s idle")
axes[0].plot(xi, values(idle, "managed_bytes", 1024**2), color=orange,
             linewidth=1.6, linestyle="--", label="Managed heap: after 20s idle")
axes[0].set_ylabel("Memory (MiB)")
axes[0].legend(loc="lower left", bbox_to_anchor=(0, 1.01), ncol=3, frameon=False, fontsize=9)
axes[1].plot(x, values(rows, "pending_dispatcher"), color=gray,
             linewidth=.7, label="All samples")
axes[1].plot(xi, values(idle, "pending_dispatcher"), color=blue,
             linewidth=1.6, marker=".", markersize=3, label="After 20s idle")
axes[1].set_ylabel("Pending dispatcher operations")
axes[1].set_xlabel("Elapsed workload time (minutes)")
axes[1].legend(loc="lower left", bbox_to_anchor=(0, 1.01), ncol=2, frameon=False, fontsize=9)
for axis in axes:
    axis.set_ylim(bottom=0)
    axis.set_xlim(0, x[-1])
    axis.grid(axis="y", color="#E4E6E9", linewidth=.6)
    axis.spines[["top", "right"]].set_visible(False)
    axis.spines[["left", "bottom"]].set_color("#9298A0")
fig.suptitle(f"{next(iter(products)):,} products | WPF cart, dialog and image workload",
             x=.09, ha="left", fontsize=16, fontweight="bold")
fig.text(.09, .922, "Recorded memory and queue occupancy; no forced garbage collection", fontsize=11)
fig.text(.09, .035,
         f"Source: {args.csv.parent.name}/{args.csv.name}\n"
         f"{len(rows):,} samples; {len(idle)} completed idle intervals; last sample {x[-1]:.3f} min. "
         "Queue occupancy does not identify a retention root cause.",
         fontsize=9, color="#4C545E")
fig.subplots_adjust(left=.09, right=.98, top=.87, bottom=.14, hspace=.18)
fig.savefig(args.output, dpi=150, facecolor="white")
plt.close(fig)
print(f"PLOT_WRITTEN={args.output} samples={len(rows)} idle_intervals={len(idle)}")
