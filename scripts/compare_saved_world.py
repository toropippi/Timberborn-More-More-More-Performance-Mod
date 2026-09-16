"""Compare every world.json field, preserving array order; exclude only its export timestamp."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def load(path):
    with zipfile.ZipFile(path) as archive:
        world = json.loads(archive.read("world.json"))
        timestamp = world.pop("Timestamp")  # WorldSerializer writes DateTime.UtcNow.
        extras = {name: hashlib.sha256(archive.read(name)).hexdigest()
                  for name in archive.namelist() if name != "world.json"}
    canonical = json.dumps(world, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    return world, timestamp, hashlib.sha256(canonical).hexdigest(), extras


def compare(first, second):
    a, at, ah, ax = load(first)
    b, bt, bh, bx = load(second)
    examples = []
    count = 0

    def mismatch(path, old, new):
        nonlocal count
        count += 1
        if len(examples) < 80:
            examples.append({"path": path, "before": old, "after": new})

    def visit(old, new, path):
        if type(old) is not type(new):
            mismatch(path, str(type(old)), str(type(new)))
        elif isinstance(old, dict):
            for key in sorted(old.keys() | new.keys()):
                if key not in old or key not in new:
                    mismatch(path + "/" + key, key in old, key in new)
                else:
                    visit(old[key], new[key], path + "/" + key)
        elif isinstance(old, list):
            if len(old) != len(new):
                mismatch(path + "/length", len(old), len(new))
            for index, (left, right) in enumerate(zip(old, new)):
                visit(left, right, path + "/" + str(index))
        elif old != new:
            mismatch(path, old, new)

    if ah != bh:
        visit(a, b, "")
    return {"first": first.as_posix(), "second": second.as_posix(), "world_equal": ah == bh,
            "world_sha256": [ah, bh], "world_difference_count": count, "examples": examples,
            "excluded_world_fields": {"Timestamp": [at, bt]}, "array_order_preserved": True,
            "entities": [len(a["Entities"]), len(b["Entities"])],
            "other_archive_entries": {name: {"equal": ax.get(name) == bx.get(name), "hashes": [ax.get(name), bx.get(name)]}
                                      for name in sorted(ax.keys() | bx.keys())},
            "scope": "All serialized world state, not unsaved runtime fields or future simulation trajectory."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = compare(args.first, args.second)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key != "examples"}, ensure_ascii=False))
