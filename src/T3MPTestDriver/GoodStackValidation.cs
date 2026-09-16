using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using DeferredModels = T3MP.Loading.DeferredGoodStackModels;

namespace T3MPTestDriver;

// All fixture mutation, file output and forced materialization stay in the
// development driver. Read the active product state, not the driver's copy.
internal static class GoodStackValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static string[] Args => Environment.GetCommandLineArgs();
    private static bool Has(string flag) => Args.Contains(flag, StringComparer.OrdinalIgnoreCase);
    private static string? Argument(string flag)
    {
        var args = Args;
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 == args.Length || args[index + 1].StartsWith("-")) throw new ArgumentException("Missing " + flag);
        return args[index + 1];
    }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    private static Type? Active => AppDomain.CurrentDomain.GetAssemblies()
        .OrderBy(a => a == typeof(DeferredModels).Assembly ? 1 : 0)
        .Select(a => a.GetType(typeof(DeferredModels).FullName!))
        .FirstOrDefault(t => t != null && (bool)(t.GetProperty("Installed", All)?.GetValue(null) ?? false));
    private static object? Component(BaseComponent entity, Type type) => typeof(BaseComponent).GetMethod("GetComponent")!.MakeGenericMethod(type).Invoke(entity, null);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, All)!.GetValue(instance);
    private static string Output => Path.GetDirectoryName(Argument("-logFile") ?? throw new ArgumentException("Missing -logFile"))!;

    internal static void Report(string phase) => Active?.GetMethod("Report", All)!.Invoke(null, new object[] { phase });
    private static object? Pending(Type? feature, object model)
    {
        if (feature == null) return null;
        var table = feature.GetField("Models", All)!.GetValue(null)!;
        var arguments = new object?[] { model, null };
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!) return null;
        var entry = arguments[1]!;
        return (bool)Field(entry, "Done")! || (bool)Field(entry, "Creating")! ? null : entry;
    }

    internal static void Exercise(IReadOnlyList<EntityComponent> entities)
    {
        if (!Has("-t3mpTestGoodStackExercise")) return;
        var feature = Active;
        if (feature == null && !Has("-t3mpTestLazyGoodStackBaseline"))
            throw new InvalidOperationException("GoodStack exercise requires an active feature or explicit native baseline");
        var ownerType = Find("Timberborn.GoodStackSystem.GoodStack");
        var modelField = ownerType.GetField("_goodStackModel", All)!;
        var rootField = modelField.FieldType.GetField("_root", All)!;
        var inventoryProperty = ownerType.GetProperty("Inventory", All)!;
        var emptyProperty = inventoryProperty.PropertyType.GetProperty("IsEmpty", All)!;
        var cutType = Find("Timberborn.Cutting.Cuttable");
        var yielder = cutType.GetProperty("Yielder", All)!;
        var yield = yielder.PropertyType.GetProperty("Yield", All)!;
        var amount = yield.PropertyType.GetProperty("Amount", All)!;
        var blockModelType = Find("Timberborn.BlockObjectModelSystem.BlockObjectModel");
        var fullModel = blockModelType.GetProperty("FullModel", All)!;
        var materialType = Find("Timberborn.Rendering.EntityMaterials");
        var lightingType = Find("Timberborn.Rendering.MaterialLightingRenderers");
        var enable = ownerType.GetMethods(All).Single(m => m.Name == "EnableGoodStack" && m.GetParameters().Length == 1);
        var toggle = Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions").GetMethod("ToggleModelVisibility", All)!;
        var query = materialType.GetMethod("GetChildMaterials", All)!;
        var collect = lightingType.GetMethod("CollectRenderers", All)!;
        var planPath = Argument("-t3mpTestGoodStackPlan");
        var plannedIds = planPath == null ? null : File.ReadAllLines(planPath).Select(Guid.Parse).ToArray();
        if (plannedIds != null && (plannedIds.Length != 24 || plannedIds.Distinct().Count() != 24))
            throw new InvalidOperationException("GoodStack plan requires 24 distinct entity IDs");
        if (feature == null && plannedIds == null) throw new InvalidOperationException("Native GoodStack exercise requires the candidate's fixture plan");
        var byId = entities.ToDictionary(e => e.EntityId);
        var candidates = plannedIds == null ? entities.OrderBy(e => e.EntityId) : plannedIds.Select(id => byId[id]);
        var completed = new List<Guid>();
        var readResults = new List<string>();
        foreach (var entity in candidates)
        {
            var owner = (BaseComponent?)Component(entity, ownerType);
            var cut = (BaseComponent?)Component(entity, cutType);
            if (!owner || !cut || !(bool)emptyProperty.GetValue(inventoryProperty.GetValue(owner))!)
            {
                if (plannedIds != null) throw new InvalidOperationException("Planned GoodStack is not eligible: " + entity.EntityId);
                continue;
            }
            var goods = yield.GetValue(yielder.GetValue(cut))!;
            if ((int)amount.GetValue(goods)! <= 0)
            {
                if (plannedIds != null) throw new InvalidOperationException("Planned GoodStack has no yield");
                continue;
            }
            var model = modelField.GetValue(owner)!;
            var pending = Pending(feature, model);
            if (feature != null && (pending == null || (GameObject?)rootField.GetValue(model)))
            {
                if (plannedIds != null) throw new InvalidOperationException("Planned GoodStack was already materialized");
                continue;
            }
            var parent = (GameObject)fullModel.GetValue(Component(owner!, blockModelType))!;
            toggle.Invoke(null, new object[] { parent, false, true });
            toggle.Invoke(null, new object[] { parent, false, false });
            toggle.Invoke(null, new object[] { parent, true, false });
            if (feature != null && (Pending(feature, model) == null || (GameObject?)rootField.GetValue(model)))
                throw new InvalidOperationException("Visibility calls materialized the fixture before the tested route");
            var route = completed.Count % 3;
            var result = "void";
            switch (route)
            {
                case 0: enable.Invoke(owner, new[] { goods }); break;
                case 1:
                    var materials = new List<Material>();
                    query.Invoke(Component(owner!, materialType), new object[] { parent.transform, materials });
                    result = string.Join(";", materials.Select(m => m ? m.name + "/" + m.shader.name : "null"));
                    break;
                case 2:
                    var lighting = Component(owner!, lightingType)!;
                    collect.Invoke(lighting, null);
                    var actual = (List<MeshRenderer>)Field(lighting, "_renderers")!;
                    var disabled = (List<MeshRenderer>)Field(lighting, "_disabledRenderers")!;
                    var expected = owner!.GameObject.GetComponentsInChildren<MeshRenderer>(true).Where(r => !disabled.Contains(r));
                    if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("GoodStack renderer cache membership/order differs from hierarchy");
                    result = string.Join(";", actual.Select(r => HierarchyPath(r.transform, owner.GameObject.transform)));
                    break;
            }
            if (!(GameObject?)rootField.GetValue(model) || feature != null &&
                (Pending(feature, model) != null || !(bool)Field(pending!, "Done")!))
                throw new InvalidOperationException("GoodStack first-use route did not complete materialization: " + route);
            Debug.Log($"[T3MPSTACKEXERCISE] entity={entity.EntityId} route={route} pendingBefore={feature != null} materializedAfter=True goods={goods}");
            completed.Add(entity.EntityId);
            readResults.Add(entity.EntityId + "\t" + route + "\t" + result.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
            if (completed.Count == 24) break;
        }
        if (completed.Count != 24) throw new InvalidOperationException("Not enough pending GoodStack fixtures");
        File.WriteAllLines(Path.Combine(Output, "good-stack-fixture-plan.txt"), completed.Select(id => id.ToString()));
        File.WriteAllLines(Path.Combine(Output, "good-stack-first-use-results.tsv"), readResults);
        Debug.Log("[T3MPSTACKEXERCISE] completed=24; each route=8; verified first-use transitions=" + (feature != null ? 24 : 0) + "; copied-world diagnostic");
        Report("after-exercise");
    }

    private static string HierarchyPath(Transform node, Transform root)
    {
        var parts = new List<string>();
        for (var current = node; current != root; current = current.parent)
        {
            if (!current) throw new InvalidOperationException("Renderer outside fixture hierarchy");
            parts.Add(current.GetSiblingIndex() + ":" + current.name);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    internal static void ValidateBeforeSnapshots(IReadOnlyList<EntityComponent> entities)
    {
        if (!Has("-t3mpTestLazyGoodStackValidate")) return;
        if (Active == null && !Has("-t3mpTestLazyGoodStackBaseline"))
            throw new InvalidOperationException("GoodStack validation requires an active feature or explicit native baseline");
        Report("before-validation");
        SelectionSnapshot.Report(entities);
        var selection = Path.Combine(Output, "selection-state.tsv");
        if (File.Exists(selection)) File.Move(selection, Path.Combine(Output, "selection-lazy.tsv"));
        var feature = Active;
        var count = 0;
        if (feature != null)
        {
            var references = ((IEnumerable)feature.GetField("Entries", All)!.GetValue(null)!).Cast<object>().ToArray();
            var ensure = feature.GetMethod("Ensure", All)!;
            var root = (FieldInfo)feature.GetField("_root", All)!.GetValue(null)!;
            var inventory = (Func<object, object>)feature.GetField("_inventory", All)!.GetValue(null)!;
            var empty = (Func<object, bool>)feature.GetField("_empty", All)!.GetValue(null)!;
            foreach (var reference in references)
            {
                var arguments = new object?[] { null };
                if (!(bool)reference.GetType().GetMethod("TryGetTarget")!.Invoke(reference, arguments)!) continue;
                var entry = arguments[0]!;
                var owner = (BaseComponent)Field(entry, "Owner")!;
                if ((bool)Field(entry, "Done")! || !owner) continue;
                if (!empty(inventory(owner)) || (GameObject?)root.GetValue(Field(entry, "Model")))
                    throw new InvalidOperationException("Pending model is nonempty or already initialized");
                ensure.Invoke(null, new[] { entry, "validation" }); count++;
            }
        }
        Debug.Log("[T3MPLAZYSTACK] VALIDATE materialized=" + count + "; compare complete snapshots against native baseline; post-load work");
        Report("after-validation");
    }
}
