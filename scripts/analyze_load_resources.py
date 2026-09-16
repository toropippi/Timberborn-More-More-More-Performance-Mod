"""Extract coarse load resource data and experimental navigation results.

This does not equate CPU residency with compute-bound execution, or heap
growth with allocated bytes. Replay kernel timings are separate from load.
"""
import argparse
import json
import re
from pathlib import Path


def read_run(path):
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    if "[T3MPLOAD] COMPLETE" not in text or "[T3MPLOAD] ERROR" in text:
        raise ValueError(f"Incomplete or failed run: {path}")
    if "another patch changes delivery" in text:
        raise ValueError(f"Routing compatibility fallback contaminated run: {path}")
    resources = []
    for line in text.splitlines():
        if not line.startswith("[T3MPRESOURCE] ") or "wallMs=" not in line:
            continue
        values = dict(re.findall(r"(\w+)=([^ ]+)", line))
        row = {"stage": line.split()[1]}
        for key, value in values.items():
            if key.endswith("Ms") or key == "heapDeltaMB": row[key] = float(value)
            elif key in ("threadCycles", "gc", "allocatedBytes", "tid", "qpcStart", "qpcFrequency"): row[key] = int(value)
            else: row[key] = value
        resources.append(row)
    return {
        "log": path.as_posix(),
        "profile_only": "CONFIG profileOnly=True experimentsInstalled=False" in text,
        "status_final_state": "[T3MPSTATUSFINAL] installed enabled=True" in text,
        "status_final_state_results": [line for line in text.splitlines() if line.startswith("[T3MPSTATUSFINAL]")],
        "status_icon_staging": "[T3MPSTATUSSTAGING] installed" in text,
        "status_icon_staging_results": [line for line in text.splitlines() if line.startswith("[T3MPSTATUSSTAGING]")],
        "status_initial_state_snapshot": bool(re.search(r"\[T3MPSTATUSFINAL\] installed[^\r\n]*snapshot=True", text)),
        "navigation_live": "[T3MPNAVLIVE] phase=" in text,
        "navigation_validation": "[T3MPNAVLIVE] VALIDATE PASS" in text,
        "navigation_initial_build_engaged": any(int(x) > 0 for x in re.findall(r"initialBuilt=(\d+)", text)),
        "navigation_packed_sources": "[T3MPNAVPACKED] installed" in text,
        "navigation_packed_results": [line for line in text.splitlines() if line.startswith("[T3MPNAVPACKED]")],
        "navigation_initial_graph": "[T3MPNAVGRAPH] installed" in text,
        "navigation_initial_graph_results": [line for line in text.splitlines() if line.startswith("[T3MPNAVGRAPH]")],
        "navigation_removal_view": "[T3MPNAVREMOVAL] installed" in text,
        "navigation_removal_results": [line for line in text.splitlines() if line.startswith("[T3MPNAVREMOVAL]")],
        "memory_census_results": [line for line in text.splitlines() if line.startswith("[T3MPMEMORY]")],
        "model_variant_census_results": [line for line in text.splitlines() if line.startswith("[T3MPVARIANTCENSUS]")],
        "lazy_path_variants": "[T3MPLAZYPATH] installed" in text and not bool(re.search(r"\[T3MPLAZYPATH\] installed[^\r\n]*paths=False", text)),
        "lazy_tube_variants": "[T3MPLAZYTUBE] installed" in text,
        "lazy_tube_variant_results": [line for line in text.splitlines() if line.startswith("[T3MPLAZYTUBE]")],
        "post_load_tube_lighting_snapshot": "[T3MPTUBELIGHTING]" in text,
        "lazy_path_variant_results": [line for line in text.splitlines() if line.startswith("[T3MPLAZYPATH]")],
        "post_load_path_variant_validation": "[T3MPLAZYPATH] phase=before-validation" in text,
        "service_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPSERVICE]")],
        "event_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPEVENTPROFILE]")],
        "water_column_index": "[T3MPWATERCOLUMN] installed" in text,
        "water_column_results": [line for line in text.splitlines() if line.startswith("[T3MPWATERCOLUMN]")],
        "layered_obstacle_index": "[T3MPLAYEREDINDEX] installed" in text,
        "layered_obstacle_results": [line for line in text.splitlines() if line.startswith("[T3MPLAYEREDINDEX]")],
        "block_event_routing": "[T3MPBLOCKROUTING] installed" in text,
        "block_event_results": [line for line in text.splitlines() if line.startswith("[T3MPBLOCKROUTING]")],
        "gc_capability_results": [line for line in text.splitlines() if line.startswith("[T3MPRESOURCE] gcIncremental=")],
        "load_gc_budget": "[T3MPGCBUDGET] installed" in text,
        "load_gc_budget_results": [line for line in text.splitlines() if line.startswith("[T3MPGCBUDGET]")],
        "load_gc_budget_validation": [line for line in text.splitlines() if line.startswith("[T3MPGCBUDGETTEST]")],
        "prefab_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPPREFAB]")],
        "model_input_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPMODELINPUT]")],
        "async_clone_probe_results": [line for line in text.splitlines() if line.startswith("[T3MPASYNCCLONE]")],
        "model_visibility_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPMODELPROFILE]")],
        "visibility_writes": "[T3MPVISIBILITY] installed" in text,
        "visibility_results": [line for line in text.splitlines() if line.startswith("[T3MPVISIBILITY]")],
        "natural_model_transition": "[T3MPNATURALMODEL] installed" in text,
        "natural_model_results": [line for line in text.splitlines() if line.startswith("[T3MPNATURALMODEL]")],
        "lazy_good_stack": "[T3MPLAZYSTACK] installed" in text,
        "lazy_building_preview": any(marker in text for marker in ("[T3MPBUILDINGPREVIEW] installed enabled=True", "[T3MPBUILDINGPREVIEW] installed; first preview")),
        "building_preview_guard_audit": "[T3MPBUILDINGPREVIEW] GUARD PASS" in text,
        "post_load_building_preview_validation": "[T3MPBUILDINGPREVIEW] VALIDATE PASS" in text,
        "building_preview_results": [line for line in text.splitlines() if line.startswith("[T3MPBUILDINGPREVIEW]")],
        "lazy_plantable_preview": "[T3MPPLANTPREVIEW] installed" in text,
        "plantable_preview_results": [line for line in text.splitlines() if line.startswith("[T3MPPLANTPREVIEW]")],
        "post_load_plantable_preview_validation": "[T3MPPLANTPREVIEW] VALIDATE PASS" in text,
        "lazy_good_stack_results": [line for line in text.splitlines() if line.startswith("[T3MPLAZYSTACK]")],
        "good_stack_exercise_results": [line for line in text.splitlines() if line.startswith("[T3MPSTACKEXERCISE]")],
        "construction_links": "[T3MPDILINK] installed" in text,
        "construction_link_results": [line for line in text.splitlines() if line.startswith("[T3MPDILINK]")],
        "mesh_capacity": "[T3MPMESHCAPACITY] installed" in text,
        "mesh_capacity_results": [line for line in text.splitlines() if line.startswith("[T3MPMESHCAPACITY]")],
        "mesh_snapshot_results": [line for line in text.splitlines() if line.startswith("[T3MPMESHSTATE]")],
        "construction_plans": "[T3MPDI] executable construction plans installed" in text,
        "construction_inline_checks": "[T3MPDIINLINE] installed" in text,
        "construction_recipes": "[T3MPDIRECIPE] installed" in text,
        "construction_bound_arguments": "[T3MPDIBOUND] installed" in text,
        "construction_bound_results": [line for line in text.splitlines() if line.startswith("[T3MPDIBOUND]")],
        "post_load_construction_bound_validation": "[T3MPDIBOUND] VALIDATE PASS" in text,
        "component_recipes": "[T3MPCOMPONENTRECIPE] installed" in text,
        "adapter_templates": "[T3MPADAPTER] installed" in text,
        "deferred_update_adapters": "[T3MPDEFERREDADAPTER] installed" in text,
        "deferred_update_adapter_results": [line for line in text.splitlines() if line.startswith("[T3MPDEFERREDADAPTER]")],
        "post_load_deferred_update_adapter_validation": "[T3MPDEFERREDADAPTER] VALIDATE PASS" in text,
        "adapter_template_results": [line for line in text.splitlines() if line.startswith("[T3MPADAPTER]")],
        "post_load_adapter_validation": "[T3MPADAPTER] VALIDATE PASS" in text,
        "component_recipe_results": [line for line in text.splitlines() if line.startswith("[T3MPCOMPONENTRECIPE]")],
        "post_load_component_recipe_validation": "[T3MPCOMPONENTRECIPE] VALIDATE PASS" in text,
        "construction_recipe_results": [line for line in text.splitlines() if line.startswith("[T3MPDIRECIPE]")],
        "construction_results": [line for line in text.splitlines() if line.startswith("[T3MPDI]")],
        "construction_profile_results": [line for line in text.splitlines() if line.startswith(("[T3MPCONSTRUCTION]", "[T3MPCONSTRUCTOR]"))],
        "entity_creation_profile_results": [line for line in text.splitlines() if line.startswith("[T3MPCREATION]")],
        "lazy_construction_stages": "[T3MPLAZYSTAGE] installed" in text,
        "lazy_construction_stage_results": [line for line in text.splitlines() if line.startswith("[T3MPLAZYSTAGE]")],
        "construction_stage_validation": "phase=before-validation enrolled=" in text and "[T3MPLAZYSTAGE] phase=before-validation" in text,
        "initialization_profile": "[T3MPINIT] installed=" in text,
        "event_registration_plans": "[T3MPREGPLAN] enabled=True" in text,
        "event_registration_results": [line for line in text.splitlines() if line.startswith("[T3MPREGPLAN]")],
        "visual_preparation_results": [line for line in text.splitlines() if line.startswith("[T3MPVISUALPREP]")],
        "carried_preparation_results": [line for line in text.splitlines() if line.startswith("[T3MPCARRIED]")],
        "navigation_shape_results": [line for line in text.splitlines() if line.startswith("[T3MPNAVSHAPE]")],
        "transput_incremental_results": [line for line in text.splitlines() if line.startswith("[T3MPTRANSPUTPLAN]")],
        "model_work_profile": "[T3MPMODELWORK]" in text,
        "initialization_results": [line for line in text.splitlines() if line.startswith("[T3MPINIT]")],
        "initialization_memory_results": [line for line in text.splitlines() if line.startswith("[T3MPINITMEMORY]")],
        "ranged_events": "[T3MPRANGEDEVENT] installed" in text,
        "ranged_event_results": [line for line in text.splitlines() if line.startswith("[T3MPRANGEDEVENT]")],
        "status_sprite_cache": "[T3MPSTATUSCACHE] installed" in text,
        "status_sprite_results": [line for line in text.splitlines() if line.startswith("[T3MPSTATUSCACHE]")],
        "load_gc_batch": "[T3MPLOADGC] installed" in text,
        "load_gc_results": [line for line in text.splitlines() if line.startswith("[T3MPLOADGC]")],
        "load_gc_headroom": "headroomBudget=True" in text,
        "full_state_results": [line for line in text.splitlines() if line.startswith("[T3MPFULLSTATE]")],
        "selection_results": [line for line in text.splitlines() if line.startswith("[T3MPSELECTION]")],
        "transput_routing": "[T3MPTRANSPUT] installed" in text,
        "transput_results": [line for line in text.splitlines() if line.startswith("[T3MPTRANSPUT]")],
        "model_layout": "[T3MPMODELLAYOUT] installed" in text,
        "model_layout_results": [line for line in text.splitlines() if line.startswith("[T3MPMODELLAYOUT]")],
        "model_prehide": "[T3MPPREHIDE] installed" in text,
        "model_prehide_engaged": any(int(value) > 0 for value in re.findall(r"\[T3MPPREHIDE\] instances=\d+ hidden=(\d+)", text)),
        "model_prehide_results": [line for line in text.splitlines() if line.startswith("[T3MPPREHIDE]")],
        "construction_prehide": "[T3MPCONSTRUCTIONPREHIDE] installed" in text,
        "construction_prehide_results": [line for line in text.splitlines() if line.startswith("[T3MPCONSTRUCTIONPREHIDE]")],
        "visual_results": [line for line in text.splitlines() if line.startswith("[T3MPVISUAL]")],
        "visual_seed_results": [line for line in text.splitlines() if line.startswith("[T3MPVISUALSEED]")],
        "diagnostic_random_input": "[T3MPVISUALSEED] enabled" in text,
        "whole_load_timing_includes_experiment_work": "[T3MPNAVSHAPE] installed validate=True" in text or "[T3MPINITGUARD]" in text or bool(re.search(r"\[T3MPSTATUSFINAL\] installed[^\r\n]*snapshot=True", text)) or "[T3MPLAZYPATH] installed boundsValidate=True" in text or "[T3MPBUILDINGPREVIEW] GUARD PASS" in text or "[T3MPPLANTPREVIEW] prototype" in text or "[T3MPEVENTPROFILE] installed" in text or bool(re.search(r"\[T3MP(?:WATERCOLUMN|LAYEREDINDEX|BLOCKROUTING)\] installed[^\r\n]*validate=True", text)) or any(tag in text for tag in
            ("[T3MPSERVICEWORK]", "[T3MPTERRAINSURFACETEST]", "[T3MPATLASPIXELSTEST]", "[T3MPNAVNOTIFY]", "[T3MPINITIALNAVTEST]", "[T3MPNAVREPLAY]", "[T3MPNAVLIVE] VALIDATE PASS", "[T3MPCONSTRUCTION]", "[T3MPINIT]", "[T3MPDI] VALIDATE PASS", "[T3MPSTATUSCACHE] installed validate=True", "[T3MPTRANSPUT] installed validate=True", "[T3MPMODELLAYOUT] installed validate=True", "[T3MPSERVICE] caller-site", "[T3MPPREFAB] installed", "[T3MPMODELINPUT] installed", "[T3MPMODELPROFILE] installed")),
        "initial_navigation_results": [line for line in text.splitlines() if line.startswith("[T3MPINITIALNAV]")],
        "initial_navigation_validation": "[T3MPINITIALNAVTEST]" in text,
        "post_load_construction_recipe_validation": "[T3MPDIRECIPE] VALIDATE PASS" in text,
        "post_load_visual_snapshot": "[T3MPVISUAL]" in text,
        "game": re.search(r"Starting game version ([^\r\n]+)", text).group(1),
        "scene_load_ms": [int(x) for x in re.findall(r"Load time: (\d+)ms \(scene index: 2\)", text)],
        "resources": resources,
        "nav_results": [line for line in text.splitlines() if line.startswith(("[T3MPNAVREPLAY]", "[T3MPNAVLIVE]"))],
        "navigation_notification_profile": [line for line in text.splitlines() if line.startswith("[T3MPNAVNOTIFY]")],
        "empty_flow_results": [line for line in text.splitlines() if line.startswith("[T3MPEMPTYFLOW]")],
        "service_work_profile": [line for line in text.splitlines() if line.startswith("[T3MPSERVICEWORK]")],
        "terrain_surface_results": [line for line in text.splitlines() if line.startswith("[T3MPTERRAINSURFACE]")],
        "atlas_pixels_results": [line for line in text.splitlines() if line.startswith("[T3MPATLASPIXELS]")],
        "state": [line for line in text.splitlines() if line.startswith("[T3MPLOAD] STATE")],
        "notes": {
            "scope_totals_overlap": True,
            "cpu_time_does_not_distinguish_memory_stalls_from_execution": True,
            "allocated_bytes_counter_supported": "allocationCounterProbe=0 " not in text if "allocationCounterProbe=" in text else None,
            "heap_delta_is_not_allocation_volume": True,
            "replay_is_not_total_load_time": True,
        },
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = {"runs": [read_run(path) for path in args.logs]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
