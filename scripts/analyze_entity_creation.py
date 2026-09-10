"""Extract caller-site creation attribution; GC-overlap calls are not GC pauses."""
import argparse
import json
import re
from pathlib import Path


def read_run(path):
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    if "[T3MPLOAD] COMPLETE" not in text or "[T3MPLOAD] ERROR" in text:
        raise ValueError(f"Incomplete or failed run: {path}")
    profile = []
    totals = []
    resources = []
    for line in text.splitlines():
        if not (line.startswith("[T3MPCREATION]") or
                line.startswith("[T3MPRESOURCE] WorldEntitiesLoader.InstantiateEntities ")):
            continue
        row = dict(re.findall(r"(\w+)=([^ ]+)", line))
        for key, value in row.items():
            if key.endswith("Ms") or key in ("ms", "heapDeltaMB"):
                row[key] = float(value)
            elif key in ("calls", "gcCalls", "openScopes", "gc", "threadCycles"):
                row[key] = int(value)
        if line.startswith("[T3MPRESOURCE]"):
            resources.append(row)
        elif "method" in row:
            profile.append(row)
        elif "totalMs" in row:
            if row["openScopes"] or row["failed"] != "False":
                raise ValueError(f"Unbalanced or failed profile: {path}")
            totals.append(row)
    if totals:
        if len(totals) != 1:
            raise ValueError(f"Expected one entity phase: {path}")
        total = totals[0]
        if abs(sum(row["ownMs"] for row in profile) - total["measuredOwnMs"]) > 0.025:
            raise ValueError(f"Exclusive accounting mismatch: {path}")
        if abs(total["totalMs"] - total["measuredOwnMs"] - total["remainderMs"]) > 0.002:
            raise ValueError(f"Phase accounting mismatch: {path}")
    return {
        "log": path.as_posix(), "diagnostic_attribution": bool(totals),
        "entity_resources": resources, "totals": totals, "methods": profile,
        "scene_ms": [int(x) for x in re.findall(r"Load time: (\d+)ms \(scene index: 2\)", text)],
        "engagement": [line for line in text.splitlines() if line.startswith((
            "[T3MPVISUALPREP] natural=", "[T3MPNAVSHAPE] hits=", "[T3MPTRANSPUT] events=",
            "[T3MPLAZYSTACK] phase=load-end", "[T3MPLOAD] SMOKE PASS"))],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = {"runs": [read_run(path) for path in args.logs], "limitations": [
        "Method scopes are wall time with observer overhead, not clean CPU timings.",
        "Inclusive ms and gcCallMs overlap parents; only ownMs forms an exclusive partition.",
        "gcCallMs includes all work in calls containing collections, not isolated GC pause time.",
        "A clean control with snapshots is a semantic control, not a clean performance A/B.",
    ]}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"runs": len(result["runs"]), "output": args.output.as_posix()}))
