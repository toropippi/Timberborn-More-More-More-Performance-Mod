"""Extract additive initialization attribution, separate from clean load timing."""
import argparse
import json
from pathlib import Path
import re


def read_log(path):
    text = path.read_text(encoding="utf-8-sig")
    if "[T3MPLOAD] COMPLETE" not in text or "[T3MPLOAD] ERROR" in text:
        raise ValueError("Incomplete or failed diagnostic")
    phases = {}
    for match in re.finditer(r"\[T3MPINITPHASE\] phase=(\w+) totalMs=([\d.]+) measuredOwnMs=([\d.]+) remainderMs=([\d.]+)", text):
        name, total, measured, remainder = match.groups()
        if name in phases:
            raise ValueError("Repeated phase summary")
        phases[name] = dict(totalMs=float(total), measuredOwnMs=float(measured),
                            remainderMs=float(remainder), methods=[])
    for match in re.finditer(r"\[T3MPINITPHASE\] phase=(\w+) method=(\S+) calls=(\d+) ms=([\d.]+) ownMs=([\d.]+)", text):
        phase, method, calls, elapsed, own = match.groups()
        phases[phase]["methods"].append(dict(method=method, calls=int(calls), ms=float(elapsed), ownMs=float(own)))
    if set(phases) != {"PreInitialize", "Initialize", "PostInitialize"}:
        raise ValueError("Missing phase")
    for phase in phases.values():
        if abs(sum(m["ownMs"] for m in phase["methods"]) - phase["measuredOwnMs"]) > 1:
            raise ValueError("Own-time accounting does not reconcile within log rounding")
    return {
        "log": path.as_posix(),
        "metric": "Instrumented elapsed milliseconds; own excludes measured children; never clean load timing",
        "phases": phases,
        "totalMs": sum(p["totalMs"] for p in phases.values()),
        "deferred_hooks": "component hooks deferred until after creation" in text,
        "engagement": [line for line in text.splitlines() if line.startswith((
            "[T3MPVISUALPREP]", "[T3MPSTATUSSTAGING] clones=", "[T3MPTRANSPUT] events=", "[T3MPBLOCKROUTING] events="))],
        "warning": "Deferred hook installation/removal is outside these phase clocks but can inflate coarse caller timings. Remainder also includes instrumentation overhead.",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    data = read_log(args.log)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"totalMs": data["totalMs"], "phases": {k: v["totalMs"] for k, v in data["phases"].items()}}))
