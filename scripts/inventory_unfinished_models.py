"""Inventory #Unfinished subtrees in an existing full model snapshot, read-only.

This is a named-subtree census, not measured creation cost or a proof that a
subtree can be removed. Finished defaults match reviewed game 1.1 save loading.
"""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import zipfile


def inventory(save, snapshot):
    with zipfile.ZipFile(save) as archive:
        entities = {e["Id"]: e for e in json.loads(archive.read("world.json"))["Entities"]}
    by_template = defaultdict(Counter)
    totals = Counter()
    branch = None
    inside = False
    entity = None
    for line in snapshot.open(encoding="utf-8-sig"):
        if line.startswith("ENTITY "):
            entity = entities[line.strip()[7:]]
            branch = None
            inside = False
            continue
        if line.startswith("root"):
            path, name, active, hierarchy, _ = line.rstrip().split("|", 4)
            totals["all_nodes"] += 1
            totals["all_inactive_nodes"] += hierarchy == "False"
            if name == "#Unfinished":
                branch = path
                totals["named_roots"] += 1
                if entity["Components"].get("BlockObjectState", {}).get("Finished", True):
                    by_template[entity["Template"]]["roots"] += 1
            inside = branch is not None and (path == branch or path.startswith(branch + "/"))
            if inside and entity["Components"].get("BlockObjectState", {}).get("Finished", True):
                row = by_template[entity["Template"]]
                row["nodes"] += 1
                row["inactive_nodes"] += hierarchy == "False"
        elif inside and entity["Components"].get("BlockObjectState", {}).get("Finished", True):
            row = by_template[entity["Template"]]
            if line.startswith("renderer|"):
                row["renderers"] += 1
                row["enabled_renderers"] += line.split("|")[2] == "True"
            elif line.startswith("collider-state|"):
                row["colliders"] += 1
                row["enabled_colliders"] += line.split("|")[2] == "True"
    selected = sum(by_template.values(), Counter())
    for key in ("enabled_renderers", "enabled_colliders"):
        selected[key] = selected[key]
    return {"save": save.as_posix(), "snapshot": snapshot.as_posix(), "totals": dict(totals),
            "finished_entity_unfinished_subtrees": dict(selected),
            "by_template": dict(sorted(by_template.items(), key=lambda p: -p[1]["nodes"])),
            "limitations": ["Exact name #Unfinished identifies candidates in this reviewed snapshot, not arbitrary game specs.",
                            "Counts are post-load hierarchy records, not a timed count of InstantiateEntities allocations.",
                            "Inactive or hidden does not establish no references, no future use or safe deletion.",
                            "Colliders use one collider-state record per component; box geometry detail rows are not double-counted."]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("save", type=Path)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = inventory(args.save, args.snapshot)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "by_template"}))
