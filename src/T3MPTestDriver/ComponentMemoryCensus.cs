using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using Bindito.Core;
using UnityEngine;

namespace T3MPTestDriver;

internal static class ComponentMemoryCensus
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private sealed class Identity : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object x) => RuntimeHelpers.GetHashCode(x);
    }
    private sealed class Row { internal long References, Unique, Null, Empty, Count, Capacity; }
    internal static void Report(IEnumerable<EntityComponent> entities, IContainer container)
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestMemoryCensus")) return;
        ReportObjects(entities.SelectMany(e => e.AllComponents), "component");
        var roots = container.GetInstance<ISingletonRepository>().GetSingletons<object>().ToArray();
        var services = new HashSet<object>(new Identity());
        var pending = new Queue<(object Value, int Depth)>();
        foreach (var root in roots) pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (value, depth) = pending.Dequeue();
            if (!services.Add(value) || depth == 2) continue;
            for (var t = value.GetType(); t != null; t = t.BaseType)
            foreach (var field in t.GetFields(Fields))
            {
                if (field.FieldType.IsValueType || !(field.FieldType.Namespace?.StartsWith("Timberborn.", StringComparison.Ordinal) ?? false)) continue;
                var child = field.GetValue(value);
                if (child != null && !(child is UnityEngine.Object) && !(child is Timberborn.BaseComponentSystem.BaseComponent)) pending.Enqueue((child, depth + 1));
            }
        }
        ReportObjects(services, "service-depth2");
    }
    private static void ReportObjects(IEnumerable<object> objects, string scope)
    {
        var args = Environment.GetCommandLineArgs();
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        var seenComponents = new HashSet<object>(new Identity());
        var seenContainers = new HashSet<object>(new Identity());
        var types = new Dictionary<Type, Row>();
        var fields = new Dictionary<Type, FieldInfo[]>();
        var rows = new Dictionary<FieldInfo, Row>();
        foreach (var component in objects)
        {
            var type = component.GetType();
            if (!types.TryGetValue(type, out var total)) types.Add(type, total = new Row());
            total.References++;
            if (!seenComponents.Add(component)) continue;
            total.Unique++;
            if (!fields.TryGetValue(type, out var selected))
            {
                var list = new List<FieldInfo>();
                for (var t = type; t != null; t = t.BaseType)
                    list.AddRange(t.GetFields(Fields).Where(f => !f.FieldType.IsValueType &&
                        (f.FieldType.IsArray || (f.FieldType.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) ?? false))));
                fields.Add(type, selected = list.ToArray());
            }
            foreach (var field in selected)
            {
                if (!rows.TryGetValue(field, out var row)) rows.Add(field, row = new Row());
                row.References++;
                var value = field.GetValue(component);
                if (value == null) { row.Null++; continue; }
                var containerType = value.GetType();
                if (!(value is Array) && !(containerType.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) ?? false)) continue;
                if (!seenContainers.Add(value)) continue;
                row.Unique++;
                long count;
                if (value is ICollection collection) count = collection.Count;
                else
                {
                    var property = containerType.GetProperty("Count");
                    if (property?.PropertyType != typeof(int)) continue;
                    count = (int)property.GetValue(value)!;
                }
                row.Count += count;
                if (count == 0) row.Empty++;
                if (value is Array array) row.Capacity += array.LongLength;
                else
                {
                    var backing = containerType.GetFields(Fields).FirstOrDefault(f => f.FieldType.IsArray &&
                        (f.Name == "_items" || f.Name == "_entries" || f.Name == "_slots"));
                    if (backing?.GetValue(value) is Array storage) row.Capacity += storage.LongLength;
                }
            }
        }
        using (var writer = new StreamWriter(Path.Combine(directory, scope + "-counts.tsv")))
        {
            writer.WriteLine("type\treferences\tunique");
            foreach (var item in types.OrderByDescending(x => x.Value.Unique))
                writer.WriteLine($"{item.Key.FullName}\t{item.Value.References}\t{item.Value.Unique}");
        }
        using (var writer = new StreamWriter(Path.Combine(directory, scope + "-collections.tsv")))
        {
            writer.WriteLine("field\treferences\tunique\tnull\tempty\tcount\tcapacity");
            foreach (var item in rows.OrderByDescending(x => x.Value.Capacity))
            {
                var r = item.Value;
                writer.WriteLine($"{item.Key.DeclaringType!.FullName}.{item.Key.Name}\t{r.References}\t{r.Unique}\t{r.Null}\t{r.Empty}\t{r.Count}\t{r.Capacity}");
            }
        }
        Debug.Log($"[T3MPMEMORY] scope={scope} profilerSupported={UnityEngine.Profiling.Profiler.supported} references={types.Values.Sum(x => x.References)} uniqueObjects={seenComponents.Count} uniqueDirectCollections={seenContainers.Count}; scoped census, not full heap size");
    }
}
