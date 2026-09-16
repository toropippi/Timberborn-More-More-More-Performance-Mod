using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.Coordinates;
using UnityEngine;

namespace T3MPTestDriver;

internal static partial class LazyPathVariantExperiment
{
    private static Type _tubeType = null!, _tubeSpecType = null!, _lightingType = null!;
    private static FieldInfo _tubeModels = null!;
    private static MethodInfo _collectLighting = null!;
    private static float _lightingMultiplier;
    private static int _lightingReads;
    private static MethodInfo _setMaterialProperties = null!;
    private static FieldInfo _lightingRenderers = null!, _disabledLightingRenderers = null!;
    private sealed class LightingMember { internal MeshRenderer? Renderer; internal int Variant = -1; }
    private sealed class MaterialReplay
    {
        internal object Colorer = null!;
        internal Color? Color, LightingColor;
        internal float? Grayscale;
    }

    private static void InitializeTubeHooks()
    {
        _tubeType = Find("Timberborn.TubeSystem.TubeModel");
        _tubeSpecType = Find("Timberborn.TubeSystem.TubeModelSpec");
        _lightingType = Find("Timberborn.Rendering.MaterialLightingRenderers");
        _tubeModels = _tubeType.GetField("_models", All)!;
        _collectLighting = _lightingType.GetMethod("CollectRenderers", All)!;
        _lightingRenderers = _lightingType.GetField("_renderers", All)!;
        _disabledLightingRenderers = _lightingType.GetField("_disabledRenderers", All)!;
        _setMaterialProperties = Find("Timberborn.Rendering.MaterialColorer").GetMethod("SetCachedMaterialProperties", All)!;
        _lightingMultiplier = (float)Find("Timberborn.Rendering.MaterialLightingEnabler").GetField("LightingStrengthMultiplier", All)!.GetValue(null)!;
        Debug.Log("[T3MPLAZYTUBE] installed; static candidates, native connection maps, deferred lighting replay");
    }
    private static bool InitializeTube(BaseComponent __instance)
    {
        if (!Roots.TryGetValue(__instance.GameObject, out var entry) || !entry.Plan.Tube) return true;
        var models = (NeighboredValues6<GameObject>)_tubeModels.GetValue(__instance)!;
        for (var i = 0; i < entry.Plan.Variants.Length; i++)
        {
            var variant = entry.Plan.Variants[i];
            if (entry.Instances[i]) entry.Instances[i]!.SetActive(false);
            var m = variant.Mask;
            models.AddVariants(variant.Source, (m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0, (m & 16) != 0, (m & 32) != 0);
        }
        return false;
    }
    private static void BeforeCollectLighting(BaseComponent __instance)
    {
        if (Roots.TryGetValue(__instance.GameObject, out var entry) && entry.Plan.Tube) entry.LightingOwner = __instance;
    }
    private static void AfterCollectLighting(BaseComponent __instance)
    {
        if (!Roots.TryGetValue(__instance.GameObject, out var entry) || !entry.Plan.Tube) return;
        var included = new HashSet<MeshRenderer>((List<MeshRenderer>)_lightingRenderers.GetValue(__instance)!);
        var disabled = (List<MeshRenderer>)_disabledLightingRenderers.GetValue(__instance)!;
        var members = new List<LightingMember>();
        void Gather(Transform node)
        {
            foreach (var renderer in node.GetComponents<MeshRenderer>())
                if (included.Contains(renderer)) members.Add(new LightingMember { Renderer = renderer });
            if (node == entry.Parent)
            {
                var actualIndex = 0;
                foreach (var i in entry.Plan.ParentSlots)
                {
                    if (i < 0) Gather(node.GetChild(actualIndex++));
                    else
                    {
                        var current = entry.Instances[i] ? entry.Instances[i]!.GetComponentInChildren<MeshRenderer>(true) : null;
                        if (!current || !disabled.Contains(current)) members.Add(new LightingMember { Variant = i });
                        if (current) actualIndex++;
                    }
                }
            }
            else for (var i = 0; i < node.childCount; i++) Gather(node.GetChild(i));
        }
        Gather(entry.Root.transform);
        entry.LightingMembership = members;
    }
    private static void RefreshLightingMembership(Entry entry)
    {
        if (entry.LightingOwner == null || entry.LightingMembership == null) return;
        var renderers = (List<MeshRenderer>)_lightingRenderers.GetValue(entry.LightingOwner)!;
        var disabled = (List<MeshRenderer>)_disabledLightingRenderers.GetValue(entry.LightingOwner)!;
        renderers.Clear();
        foreach (var member in entry.LightingMembership)
        {
            var renderer = member.Variant < 0 ? member.Renderer : entry.Instances[member.Variant] ?
                entry.Instances[member.Variant]!.GetComponentInChildren<MeshRenderer>(true) : null;
            if (renderer && !disabled.Contains(renderer)) renderers.Add(renderer);
        }
    }
    private static void BeforeLightingList(BaseComponent __instance)
    {
        if (_lightingReads == 0 && Roots.TryGetValue(__instance.GameObject, out var entry) && entry.Plan.Tube)
            EnsureAll(entry, "lighting-list-reader");
    }
    private static void BeforeLightingBase(BaseComponent entity, float? strength, out bool __state)
    {
        __state = false;
        if (Roots.TryGetValue(entity.GameObject, out var entry) && entry.Plan.Tube)
        {
            entry.HasLighting = true; entry.LightingValue = (uint)((strength ?? 1f) * _lightingMultiplier);
            _lightingReads++; __state = true;
        }
    }
    private static void AfterLightingBase(bool __state) { if (__state) _lightingReads--; }
    private static void BeforeLightingObject(GameObject target, float? strength)
    {
        if (!target || !Ancestors.TryGetValue(target, out var bucket)) return;
        foreach (var weak in bucket.Entries)
        {
            if (!weak.TryGetTarget(out var entry) || !entry.Root || entry.Creating) continue;
            if (entry.Plan.Tube && (target == entry.Root || target == entry.Parent.gameObject))
            { entry.HasLighting = true; entry.LightingValue = (uint)((strength ?? 1f) * _lightingMultiplier); }
            else EnsureAll(entry, "lighting-object-reader");
        }
    }
    private static void BeforeDisableLightingChanges(GameObject root) => BeforeObjectRead(root);
    private static void BeforeMaterialProperties(object __instance, GameObject root, Color? color, float? grayscale, Color? lightingColor)
    {
        if (!root || !Ancestors.TryGetValue(root, out var bucket)) return;
        foreach (var weak in bucket.Entries)
        {
            if (!weak.TryGetTarget(out var entry) || !entry.Root || entry.Creating) continue;
            if (entry.Plan.Tube && (root == entry.Root || root == entry.Parent.gameObject))
            {
                if (entry.Instances.All(g => g)) { entry.MaterialReplays = null; continue; }
                // Bound retained history without dropping any native operation.
                if (entry.MaterialReplays?.Count >= 64)
                { EnsureAll(entry, "material-history-limit"); entry.MaterialReplays = null; continue; }
                if (entry.MaterialReplays == null) entry.MaterialReplays = new System.Collections.Generic.List<MaterialReplay>();
                entry.MaterialReplays.Add(new MaterialReplay { Colorer = __instance, Color = color, Grayscale = grayscale, LightingColor = lightingColor });
            }
            else EnsureAll(entry, "material-properties-reader");
        }
    }
}
