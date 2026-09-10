"""Count serialized entity records without launching or modifying the game."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import zipfile


def count(path):
    before = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    with zipfile.ZipFile(path) as archive:
        world = json.loads(archive.read("world.json"))
    entities = world["Entities"]
    templates = Counter(e["Template"] for e in entities)
    ids = Counter(e["Id"] for e in entities)
    groups = {
        "trees": {"Oak", "Pine", "Mangrove", "Birch"},
        "bushes": {"BlueberryBush", "CoffeeBush"},
        "crops": {"Corn", "Cassava", "Kohlrabi", "Canola", "Soybean", "Eggplant"},
        "platforms": {"Platform.IronTeeth", "DoublePlatform.IronTeeth", "TriplePlatform.IronTeeth"},
        "levees": {"Levee.IronTeeth"},
        "paths": {"Path"},
        "tubeways_and_stations": {"Tubeway.IronTeeth", "VerticalTubeway.IronTeeth", "TubewayStation.IronTeeth"},
        "power_shafts": {"PowerShaft.IronTeeth", "VerticalPowerShaft.IronTeeth"},
        "beavers": {"BeaverAdult", "BeaverChild"},
        "bots": {"Bot.IronTeeth"},
    }
    assigned = set().union(*groups.values())
    groups["other_iron_teeth_structures"] = {t for t in templates if t.endswith(".IronTeeth") and t not in assigned}
    assigned.update(groups["other_iron_teeth_structures"])
    groups["other_templates"] = set(templates) - assigned
    counts = {name: sum(templates[t] for t in members) for name, members in groups.items()}
    assert sum(counts.values()) == len(entities)
    structures = [e for e in entities if e["Template"] == "Path" or
                  (e["Template"].endswith(".IronTeeth") and e["Template"] != "Bot.IronTeeth")]
    # Verified against game 1.1.2.0 BlockObjectState.Load: missing component or
    # missing Finished property means Finished, not an unfinished blueprint.
    unfinished = Counter(e["Template"] for e in structures
                         if e["Components"].get("BlockObjectState", {}).get("Finished", True) is False)
    after = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    assert before == after, "Source changed during analysis"
    return {
        "source": str(path.resolve()), "source_sha256": before,
        "game_version": world["GameVersion"], "entity_count": len(entities),
        "unique_ids": len(ids), "duplicate_ids": {k: v for k, v in ids.items() if v > 1},
        "template_count": len(templates),
        "groups": [{"name": name, "count": n, "percent": n * 100 / len(entities),
                    "templates": {t: templates[t] for t in sorted(groups[name]) if templates[t]}}
                   for name, n in counts.items()],
        "all_templates": dict(templates.most_common()),
        "structures": {"total": len(structures), "unfinished": sum(unfinished.values()),
                       "unfinished_templates": dict(unfinished)},
        "notes": ["Entity records, not component counts or occupied voxel counts.",
                  "Template grouping is explicit and tailored to n10c/IronTeeth; inspect other_templates for other saves.",
                  "Plants may be planted or naturally occurring; origin is not inferred.",
                  "Counts do not attribute elapsed time to template types."]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("save", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = count(args.save)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "all_templates"}, ensure_ascii=False, indent=2))
