"""Analyze paired, frozen-world Harmony/direct travel-cache measurements.

Usage: python scripts/analyze_harmony_cost.py testlogs/<run>/game.log [...]
Results describe this cache-hit path only, not a whole-game A/B speedup.
"""
import argparse
import json
import random
import re
import statistics
from pathlib import Path


def fields(line):
    return dict(re.findall(r"(\w+)=([^\s]+)", line))


def analyze(path):
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    lines = [line for line in text.splitlines() if "[T3MPHARMONY]" in line]
    if not any(" COMPLETE" in line for line in lines) or any(" ERROR" in line for line in lines):
        raise ValueError(f"Incomplete/failed diagnostic: {path}")
    rate = fields(next(line for line in lines if " RATE " in line))
    runtime = fields(next(line for line in lines if " runtime " in line))
    validated = fields(next(line for line in lines if " VALIDATED " in line))
    cache = fields(next(line for line in lines if " CACHE_VERIFIED " in line))
    raw_pairs = [fields(line) for line in lines if " PAIR " in line]
    if len(raw_pairs) != int(validated["pairs"]):
        raise ValueError("Missing pairs")
    if sorted(int(p["index"]) for p in raw_pairs) != list(range(len(raw_pairs))):
        raise ValueError("Duplicate/missing pair indices")
    if int(cache["hits"]) != int(validated["callsPerBatch"]) * len(raw_pairs) * 2:
        raise ValueError("Cache-hit count mismatch")
    pairs = [p for p in raw_pairs if int(p["gcA"]) == int(p["gcB"]) == 0]
    if len(pairs) < 8:
        raise ValueError("Too few GC-free pairs")
    deltas = [float(p["harmonyNs"]) - float(p["directNs"]) for p in pairs]
    rng = random.Random(20260909)
    bootstrap = sorted(statistics.median(rng.choices(deltas, k=len(deltas))) for _ in range(10000))
    median = statistics.median(deltas)
    calls_per_second = float(rate["callsPerSecond"])
    version = re.search(r"Starting game version[^\r\n]*", text)
    return {
        "log": str(path),
        "game_version": version.group(0) if version else "unknown",
        "unity": runtime["unity"],
        "mod_mvid": runtime["modMvid"],
        "driver_mvid": runtime["driverMvid"],
        "pairs_kept": len(pairs),
        "pairs_excluded_gc": len(raw_pairs) - len(pairs),
        "inputs": int(validated["inputs"]),
        "calls_per_batch": int(validated["callsPerBatch"]),
        "cache_hits_verified": int(cache["hits"]),
        "harmony_ns_median": statistics.median(float(p["harmonyNs"]) for p in pairs),
        "direct_ns_median": statistics.median(float(p["directNs"]) for p in pairs),
        "paired_delta_ns_median": median,
        "paired_delta_ns_bootstrap_95pct": [bootstrap[249], bootstrap[9749]],
        "delta_ns_median_AB": statistics.median(float(p["deltaNs"]) for p in pairs if p["order"] == "AB"),
        "delta_ns_median_BA": statistics.median(float(p["deltaNs"]) for p in pairs if p["order"] == "BA"),
        "observed_calls_per_second": calls_per_second,
        "observed_ticks_per_second": float(rate["ticksPerSecond"]),
        "estimated_wall_ms_per_second_saved": median * calls_per_second / 1e6,
        "estimated_wall_percent_saved": median * calls_per_second / 1e7,
        "raw_pairs": raw_pairs,
        "note": "Warm-cache single-path comparison; wall saving is an extrapolation from an instrumented run. Within-run bootstrap does not capture between-run uncertainty.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, help="Also preserve the summary and raw pairs as JSON")
    args = parser.parse_args()
    report = json.dumps([analyze(path) for path in args.logs], ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(report + "\n", encoding="utf-8")
    print(report)
