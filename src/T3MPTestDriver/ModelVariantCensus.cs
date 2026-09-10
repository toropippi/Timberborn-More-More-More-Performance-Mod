using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Timberborn.EntitySystem;
using UnityEngine;

namespace T3MPTestDriver;

// Post-load read-only inventory. This never changes activation, material
// ownership or the models selected by the simulation's native controllers.
internal static class ModelVariantCensus
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Row
    {
        internal string Name = "", Controller = "", Components = "";
        internal long Entities, Roots, Nodes, Renderers, Colliders, DormantNodes, DormantRenderers;
        internal readonly SortedSet<string> NativeTypes = new SortedSet<string>(StringComparer.Ordinal);
        internal readonly SortedSet<string> Selected = new SortedSet<string>(StringComparer.Ordinal);
    }

    internal static void Report(IEnumerable<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestModelVariantCensus")) return;
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var components = entity.AllComponents.ToArray();
            foreach (var model in components.Where(c => c.GetType().FullName is
                         "Timberborn.TubeSystem.TubeModel" or "Timberborn.PathSystem.DynamicPathModel"))
            {
                var type = model.GetType();
                var tube = type.Name == "TubeModel";
                var spec = type.GetField(tube ? "_tubeModelSpec" : "_dynamicPathModelSpec", All)!.GetValue(model)!;
                string Prefix(string name) => (string?)spec.GetType().GetProperty(name, All)!.GetValue(spec) ?? "";
                var prefixes = tube ? new[] { Prefix("ModelPrefix") } : new[] { Prefix("GroundModelPrefix"), Prefix("RoofModelPrefix") };
                var current = (GameObject?)type.GetField("_currentModel", All)!.GetValue(model);
                var names = string.Join(",", components.Select(c => c.GetType().FullName).OrderBy(n => n, StringComparer.Ordinal));
                var key = entity.GameObject.name + "|" + type.FullName + "|" + names;
                if (!rows.TryGetValue(key, out var row)) rows.Add(key, row = new Row
                { Name = entity.GameObject.name, Controller = type.FullName!, Components = names });
                row.Entities++;
                if (current) row.Selected.Add(current!.name);
                bool Match(Transform child) => child != entity.Transform && prefixes.Any(p => p.Length > 0 && child.name.StartsWith(p) &&
                    (tube || new[] { "0000", "0010", "1010", "0011", "0111", "1111" }.Contains(child.name.Substring(p.Length))));
                var roots = entity.GameObject.GetComponentsInChildren<Transform>(true).Where(Match).ToArray();
                // Count each descendant once even for nested matching names.
                var visited = new HashSet<Transform>();
                row.Roots += roots.Length;
                foreach (var root in roots)
                foreach (var child in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!visited.Add(child)) continue;
                    var dormant = !child.gameObject.activeInHierarchy;
                    row.Nodes++; if (dormant) row.DormantNodes++;
                    foreach (var component in child.GetComponents<Component>())
                    {
                        row.NativeTypes.Add(component ? component.GetType().FullName! : "missing-script");
                        if (component is Renderer) { row.Renderers++; if (dormant) row.DormantRenderers++; }
                        if (component is Collider) row.Colliders++;
                    }
                }
            }
        }
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        using var writer = new StreamWriter(Path.Combine(directory, "model-variants.tsv"), false, new UTF8Encoding(false));
        writer.WriteLine("name\tcontroller\tentities\troots\tnodes\trenderers\tcolliders\tdormantNodes\tdormantRenderers\tnativeTypes\tselected\tcomponents");
        foreach (var r in rows.Values.OrderByDescending(r => r.Nodes))
            writer.WriteLine($"{r.Name}\t{r.Controller}\t{r.Entities}\t{r.Roots}\t{r.Nodes}\t{r.Renderers}\t{r.Colliders}\t{r.DormantNodes}\t{r.DormantRenderers}\t{string.Join(",", r.NativeTypes)}\t{string.Join(",", r.Selected)}\t{r.Components}");
        Debug.Log($"[T3MPVARIANTCENSUS] groups={rows.Count} entities={rows.Values.Sum(r => r.Entities)} nodes={rows.Values.Sum(r => r.Nodes)} renderers={rows.Values.Sum(r => r.Renderers)} dormantNodes={rows.Values.Sum(r => r.DormantNodes)} dormantRenderers={rows.Values.Sum(r => r.DormantRenderers)}; post-load inventory, not timing or proof of safe deferral");
    }
}
