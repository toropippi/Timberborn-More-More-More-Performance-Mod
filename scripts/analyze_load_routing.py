"""Summarize matched load-routing runs without mixing builds or fingerprints."""
import argparse
import json
import re
import statistics
from pathlib import Path


def read_run(path):
    text = path.read_text(encoding="utf-8")
    if "[T3MPLOAD] COMPLETE" not in text or re.search(
        r"\[T3MPLOAD\] ERROR|First uncaught exception|Failed to patch|Load event routing disabled", text
    ):
        raise ValueError(f"Incomplete or failed run: {path}")
    baseline, mvid = re.search(r"\[T3MPLOAD\] CONFIG baseline=(True|False) modMvid=([\w-]+)", text).groups()
    state = re.search(r"\[T3MPLOAD\] STATE entities=(\d+) trackers=(\d+) inTube=(\d+) sha256=(\w+)", text).groups()
    phases = re.findall(r"LoadStage ([\w.]+) ms=([\d.]+), frame=2", text)
    event_ms = [float(ms) for name, ms in phases if name == "SingletonSystem.EventBus.PostLoad"]
    return {
        "log": path.as_posix(),
        "game": re.search(r"Starting game version ([^\r\n]+)", text)[1],
        "mvid": mvid,
        "baseline": baseline == "True",
        "load_ms": int(re.search(r"Load time: (\d+)ms \(scene index: 2\)", text)[1]),
        "event_ms": event_ms[0] if event_ms else None,
        "state": dict(zip(("entities", "trackers", "in_tube", "sha256"), state)),
        "phases_ms": {name: float(ms) for name, ms in phases},
        "tests": re.findall(r"\[T3MPLOAD\] TEST (.+) PASS", text),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    runs = [read_run(path) for path in args.logs]
    groups = {}
    for run in runs:
        groups.setdefault((run["game"], run["mvid"]), []).append(run)
    summaries = []
    for (game, mvid), items in groups.items():
        states = {tuple(run["state"].items()) for run in items}
        if len(states) != 1:
            raise ValueError(f"Loaded state differs within {game}/{mvid}")
        summary = {"game": game, "mvid": mvid, "state_matches": True}
        for label, baseline in (("baseline", True), ("optimized", False)):
            side = [run for run in items if run["baseline"] == baseline]
            if not side:
                raise ValueError(f"Missing {label} runs for {game}/{mvid}")
            summary[label] = {
                "count": len(side),
                "load_ms": [run["load_ms"] for run in side],
                "median_load_ms": statistics.median(run["load_ms"] for run in side),
                "event_ms": [run["event_ms"] for run in side],
            }
            if all(run["event_ms"] is not None for run in side):
                summary[label]["median_event_ms"] = statistics.median(run["event_ms"] for run in side)
        a = summary["baseline"]["median_load_ms"]
        b = summary["optimized"]["median_load_ms"]
        summary["median_load_reduction_ms"] = a - b
        summary["median_load_reduction_percent"] = 100 * (a - b) / a
        summaries.append(summary)
    output = {"note": "Small matched samples; medians are descriptive, not a universal speedup guarantee.",
              "summaries": summaries, "runs": runs}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summaries, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
