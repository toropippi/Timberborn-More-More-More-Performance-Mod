"""Compare every recorded native ray field exactly; never apply a tolerance."""
import argparse
from collections import Counter
import hashlib
from itertools import zip_longest
import json
from pathlib import Path


def compare(first: Path, second: Path):
    names = ("query_entity", "direction", "origin", "hit_owner_path", "distance", "point", "normal")
    changed = Counter()
    examples = []
    rows = differences = 0
    max_distance_delta = 0.0
    with first.open(encoding="utf-8-sig") as a, second.open(encoding="utf-8-sig") as b:
        for row, (left, right) in enumerate(zip_longest(a, b), 1):
            rows += 1
            if left == right:
                continue
            differences += 1
            if left is None or right is None:
                changed["missing_row"] += 1
            else:
                x, y = left.rstrip("\n").split("\t"), right.rstrip("\n").split("\t")
                if len(x) != len(y):
                    changed["hit_or_miss_layout"] += 1
                for i, (old, new) in enumerate(zip_longest(x, y)):
                    if old != new:
                        changed[names[i] if i < len(names) else f"extra_column_{i}"] += 1
                if len(x) == len(y) == 7:
                    max_distance_delta = max(max_distance_delta, abs(float(x[4]) - float(y[4])))
            if len(examples) < 20:
                examples.append({"row": row, "first": left.rstrip("\n") if left else None,
                                 "second": right.rstrip("\n") if right else None})
    def digest(path):
        with path.open("rb") as f:
            return hashlib.file_digest(f, "sha256").hexdigest()
    return {"first": first.as_posix(), "second": second.as_posix(),
            "sha256": [digest(first), digest(second)], "rows": rows,
            "equal": differences == 0, "different_rows": differences,
            "changed_fields": dict(changed), "max_distance_delta": max_distance_delta,
            "examples": examples, "notes": "No rounding, tolerance or excluded fields. Distance deltas are descriptive only."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = compare(args.first, args.second)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "examples"}, ensure_ascii=False))
