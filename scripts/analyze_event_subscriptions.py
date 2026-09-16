"""Summarize intrusive event-subscription attribution; never a speed benchmark."""
import argparse
import csv
import json
import re
from collections import defaultdict
from pathlib import Path


def analyze(path):
    text = path.read_text(encoding="utf-8-sig")
    if "[T3MPSUBSCRIBE] removedBeforeEventDrain=True" not in text:
        raise ValueError(f"Incomplete registration profile: {path}")
    rows = []
    for phase, method, calls, ms, own, failed in re.findall(
        r"\[T3MPSUBSCRIBE\] key=([^|]+)\|(\S+) calls=(\d+) ms=([\d.]+) ownMs=([\d.]+) failed=(\d+)", text
    ):
        rows.append(dict(phase=phase, method=method, calls=int(calls),
                         inclusive_ms=float(ms), own_ms=float(own), failed=int(failed)))
    totals = defaultdict(lambda: dict(calls=0, inclusive_ms=0, own_ms=0))
    for row in rows:
        for key in totals[row["method"]]:
            totals[row["method"]][key] += row[key]
    if totals["EventBus.RegisterMethod"]["calls"] < 1:
        raise ValueError(f"No world registration observations: {path}")
    census_path = path.parent / "delegate-fields.tsv"
    census = []
    if census_path.exists():
        with census_path.open(encoding="utf-8-sig", newline="") as f:
            for row in csv.DictReader(f, delimiter="\t"):
                census.append({key: value if key == "field" else int(value)
                               for key, value in row.items()})
    accessor_rows = [r for r in rows if ".add_" in r["method"] or ".remove_" in r["method"]]
    return dict(
        log=str(path), diagnostic_only=True, includes_instrumentation_overhead=True,
        registration_scope="LoadAll through just before world EventBus.PostLoad; excludes earlier container creation and later registration",
        ranged_optimized="[T3MPRANGEDEVENT] installed" in text,
        rows=rows, totals=dict(totals),
        selected_accessor_calls=sum(r["calls"] for r in accessor_rows),
        selected_accessor_own_ms=sum(r["own_ms"] for r in accessor_rows),
        subscriber_types={name: int(count) for name, count in re.findall(
            r"\[T3MPSUBSCRIBE\] subscriberType=(\S+) handlers=(\d+)", text)},
        registry={name: int(count) for name, count in re.findall(
            r"\[T3MPSUBSCRIBE\] registry=([^|\r\n]+)\|(\d+)", text)},
        delegate_fields=census,
        routes=re.findall(r"\[T3MP\] Load event routing: events=\d+[^\r\n]*", text),
        status_staging=re.findall(r"\[T3MPSTATUSSTAGING\] clones=[^\r\n]*", text),
        ranged_results=re.findall(r"\[T3MPRANGEDEVENT\] stage=[^\r\n]*", text),
        services=re.findall(r"\[T3MPSERVICE\] Timberborn.SingletonSystem.EventBus.PostLoad[^\r\n]*", text),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = {"notes": ["Nested inclusive timings must not be added.",
                         "Accessor coverage is selected, not all C# events.",
                         "Direct delegate-field census counts occurrences and misses indirect/custom stores.",
                         "A proxy represents an optimized store; invocation-list length does not count its real subscribers."],
              "runs": [analyze(path) for path in args.logs]}
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    for run in result["runs"]:
        print(Path(run["log"]).parent.name, "ranged_optimized=", run["ranged_optimized"],
              "registration=", run["totals"]["EventBus.Register"],
              "selected_accessors_ms=", round(run["selected_accessor_own_ms"], 3))


if __name__ == "__main__":
    main()
