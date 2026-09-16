"""Compare exact ray fields using an explicit source-layout identity map.

Keep the raw comparison alongside this report. Only the hit transform's path
may be mapped, and only via a row captured from the live experiment's original
template slot. No fields, names, distances, coordinates or normals are ignored.
"""
import argparse
import csv
from itertools import zip_longest
import json
from pathlib import Path


def compare(first, second, layout):
    mapping = {}
    destinations = set()
    with layout.open(encoding="utf-8-sig", newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            actual, original = row["actual"], row["original"]
            if actual in mapping or original in destinations:
                raise ValueError("Layout map is not one-to-one")
            # The map must preserve entity identity and every hierarchy name.
            def identity(path):
                parts = path.split("/")
                return [parts[0]] + [s.split(":", 1)[1] for s in parts[1:]]
            if identity(actual) != identity(original):
                raise ValueError("Layout map changes entity or node names")
            mapping[actual] = original
            destinations.add(original)
    rows = changed = mapped = 0
    examples = []
    with first.open(encoding="utf-8-sig") as a, second.open(encoding="utf-8-sig") as b:
        for left, right in zip_longest(a, b):
            rows += 1
            x = left.rstrip("\r\n").split("\t") if left is not None else None
            y = right.rstrip("\r\n").split("\t") if right is not None else None
            if y is not None and len(y) == 7 and y[3] in mapping:
                original = mapping[y[3]]
                mapped += original != y[3]
                y[3] = original
            if x != y:
                changed += 1
                if len(examples) < 10: examples.append({"row": rows, "first": x, "mapped_second": y})
    return {"first": str(first), "second": str(second), "layout": str(layout),
            "rows": rows, "mapped_rows": mapped, "layout_entries": len(mapping),
            "equal": changed == 0, "different_rows": changed, "examples": examples,
            "scope": "Exact ray fields after explicit live source-slot identity mapping; raw sibling indices are retained in the separate raw comparison."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    parser.add_argument("layout", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = compare(args.first, args.second, args.layout)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "examples"}, ensure_ascii=False))
