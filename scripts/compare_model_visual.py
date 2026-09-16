"""Compare canonical visual snapshots without silently ignoring variable fields."""
import argparse
from collections import Counter
import json
from pathlib import Path


def entities(path):
    entity_id, lines = None, []
    with path.open(encoding="utf-8") as source:
        for line in source:
            line = line.rstrip("\n")
            if line.startswith("ENTITY "):
                if entity_id is not None:
                    yield entity_id, lines
                entity_id, lines = line[7:], []
            else:
                lines.append(line)
        if entity_id is not None:
            yield entity_id, lines


def compare(first, second):
    left, right = entities(first / "model-visual-detail.txt"), entities(second / "model-visual-detail.txt")
    changed, categories, names, examples = [], Counter(), Counter(), []
    count = 0
    for a in left:
        b = next(right, None)
        if b is None or a[0] != b[0]:
            raise ValueError("Entity identity/order mismatch")
        count += 1
        if a[1] == b[1]:
            continue
        changed.append(a[0])
        if len(a[1]) != len(b[1]):
            categories["different_record_length"] += 1
            examples.append({"entity": a[0], "line_counts": [len(a[1]), len(b[1])]})
            continue
        for old, new in zip(a[1], b[1]):
            if old == new:
                continue
            old_fields, new_fields = old.split("|"), new.split("|")
            category = "other_state"
            if old.startswith("root") and new.startswith("root") and len(old_fields) == len(new_fields) == 5:
                if old_fields[:4] == new_fields[:4]:
                    category = "local_transform"
                    names[old_fields[1]] += 1
                else:
                    category = "hierarchy_or_activation"
            categories[category] += 1
            if len(examples) < 12:
                examples.append({"entity": a[0], "category": category, "before": old, "after": new})
    if next(right, None) is not None:
        raise ValueError("Extra entities in second snapshot")
    return {"first": first.as_posix(), "second": second.as_posix(), "entities": count,
            "changed_entities": changed, "changed_entity_count": len(changed),
            "changed_lines": dict(categories), "transform_changes_by_name": dict(names),
            "examples": examples, "notes": "No field is excluded; baseline variability requires independent repeated runs and source analysis."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = compare(args.first, args.second)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key not in ("changed_entities", "examples")}, ensure_ascii=False))
