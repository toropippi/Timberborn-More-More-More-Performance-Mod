using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Bindito.Core;
using Timberborn.EntitySystem;
using UnityEngine;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Post-load kernel experiment, never a replacement of live entity creation.
// All copies stay under an inactive private parent and are destroyed here.
// Benchmarked sources are private copies with the reviewed inert scripts
// removed: direct async cloning lost a mod script in the first semantic probe.
internal static class AsyncCloneProbe
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(IContainer container, IReadOnlyList<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestAsyncCloneProbe")) return;
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        using var rows = new StreamWriter(Path.Combine(directory, "async-clones.tsv"), false, new UTF8Encoding(false));
        rows.WriteLine("prefab\tnodes\tcount\tmode\ttrial\twallMs\tprocessCpuMs\tgc\tsha256");
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Timberborn.TemplateInstantiation.TemplateInstantiator")).First(t => t != null)!;
        var instance = container.GetInstance(type);
        var cache = (IDictionary)type.GetField("_cache", Fields)!.GetValue(instance)!;
        var usage = entities.GroupBy(e => e.Transform.gameObject.name.Replace("(Clone)", ""))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var candidates = new List<(GameObject Prefab, int Nodes, int Uses)>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in cache)
        {
            var prefab = (GameObject)entry.Value!.GetType().GetProperty("Prefab")!.GetValue(entry.Value)!;
            var components = prefab.GetComponentsInChildren<Component>(true);
            var unsupported = components.Where(c => !Allowed(c)).ToArray();
            if (unsupported.Length != 0)
            {
                foreach (var name in unsupported.Select(c => c ? c.GetType().FullName! : "missing script").Distinct())
                { skipped.TryGetValue(name, out var n); skipped[name] = n + 1; }
                continue;
            }
            var nodes = components.Count(c => c is Transform);
            usage.TryGetValue(prefab.name, out var uses);
            candidates.Add((prefab, nodes, uses));
        }
        Debug.Log($"[T3MPASYNCCLONE] cache={cache.Count} eligible={candidates.Count} eligibleEntities={candidates.Sum(c => c.Uses)} skipped={string.Join(",", skipped.Select(p => p.Key + ":" + p.Value))}; post-load kernel only");
        if (candidates.Count == 0) throw new InvalidOperationException("No safe cached template for clone probe");
        var savedRandom = UnityEngine.Random.state;
        var randomBefore = JsonUtility.ToJson(savedRandom);
        var parent = new GameObject("T3MP isolated clone probe");
        parent.SetActive(false);
        parent.transform.SetPositionAndRotation(new Vector3(11f, 17f, 23f), Quaternion.Euler(0f, 37f, 0f));
        parent.transform.localScale = new Vector3(2f, 3f, 4f);
        var checkedClones = 0;
        var references = new Dictionary<Object, int>();
        try
        {
            foreach (var candidate in candidates.OrderByDescending(c => (long)c.Uses * c.Nodes).ThenBy(c => c.Prefab.name, StringComparer.Ordinal).Take(3))
            {
                var original = Signature(candidate.Prefab, references);
                var preparation = Stopwatch.StartNew();
                var prefab = Object.Instantiate(candidate.Prefab, parent.transform, false);
                prefab.name = candidate.Prefab.name;
                var scripts = prefab.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (var script in scripts)
                {
                    if (!Allowed(script)) throw new InvalidOperationException("Private source has unsafe script state");
                    Object.DestroyImmediate(script);
                }
                preparation.Stop();
                Debug.Log($"[T3MPASYNCCLONE] nativeOnly=True prefab={prefab.name} removedScripts={scripts.Length} preparationMs={preparation.Elapsed.TotalMilliseconds:F3}; preparation excluded from kernel trials");
                // The initial 256-copy timings were near Windows CPU-counter
                // granularity. Longer batches expose work beyond that noise.
                var count = Math.Max(8, Math.Min(4096, 131072 / candidate.Nodes));
                string expected;
                string expectedState;
                string expectedName;
                var warm = Object.Instantiate(prefab, parent.transform, false);
                try { expectedName = warm.name; expected = Signature(warm, references, out expectedState); CheckPlacement(warm, parent); }
                finally { Object.DestroyImmediate(warm); }
                var warmAsync = Object.InstantiateAsync(prefab, 1, new InstantiateParameters { parent = parent.transform, worldSpace = false });
                warmAsync.WaitForCompletion();
                try
                {
                    if (!warmAsync.isDone || warmAsync.Result.Length != 1)
                        throw new InvalidOperationException("Warm async clone incomplete: " + prefab.name);
                    // Observed native APIs use different suffix spacing. Restore
                    // the ordinary clone name; include this work in timed trials.
                    warmAsync.Result[0].name = expectedName;
                    var actual = Signature(warmAsync.Result[0], references, out var actualState);
                    if (actual != expected)
                    {
                        using var difference = new StreamWriter(Path.Combine(directory, "async-clone-difference.txt"), false, new UTF8Encoding(false));
                        difference.WriteLine("SYNC"); difference.Write(expectedState);
                        difference.WriteLine("ASYNC"); difference.Write(actualState);
                        throw new InvalidOperationException("Warm async clone differs: " + prefab.name);
                    }
                    CheckPlacement(warmAsync.Result[0], parent);
                }
                finally { foreach (var clone in warmAsync.Result) Object.DestroyImmediate(clone); }
                // Both implementations warmed first; ABBA keeps first/last order balanced.
                for (var trial = 0; trial < 4; trial++)
                {
                    var async = trial is 1 or 2;
                    var clones = new GameObject[count];
                    AsyncInstantiateOperation<GameObject>? operation = null;
                    try
                    {
                        GC.Collect();
                        var gc = GC.CollectionCount(0);
                        using var process = Process.GetCurrentProcess();
                        var cpu = process.TotalProcessorTime;
                        var watch = Stopwatch.StartNew();
                        if (async)
                        {
                            operation = Object.InstantiateAsync(prefab, count, new InstantiateParameters { parent = parent.transform, worldSpace = false });
                            operation.WaitForCompletion();
                            clones = operation.Result;
                            foreach (var clone in clones) clone.name = expectedName;
                        }
                        else
                            for (var i = 0; i < count; i++) clones[i] = Object.Instantiate(prefab, parent.transform, false);
                        watch.Stop();
                        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                        var collections = GC.CollectionCount(0) - gc;
                        if (clones.Length != count || (operation != null && !operation.isDone))
                            throw new InvalidOperationException("Incomplete async clones");
                        foreach (var clone in clones)
                        {
                            CheckPlacement(clone, parent);
                            if (Signature(clone, references) != expected) throw new InvalidOperationException("Clone content differs: " + prefab.name);
                            checkedClones++;
                        }
                        rows.WriteLine(string.Join("\t", prefab.name, candidate.Nodes, count, async ? "async" : "sync", trial,
                            watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), cpuMs.ToString("F3", CultureInfo.InvariantCulture), collections, expected));
                        rows.Flush();
                        Debug.Log($"[T3MPASYNCCLONE] prefab={prefab.name} uses={candidate.Uses} nodes={candidate.Nodes} count={count} mode={(async ? "async" : "sync")} trial={trial} wallMs={watch.Elapsed.TotalMilliseconds:F3} processCpuMs={cpuMs:F3} gc={collections} PASS");
                    }
                    finally
                    {
                        if (operation != null && !operation.isDone) { operation.Cancel(); operation.WaitForCompletion(); }
                        foreach (var clone in clones) if (clone) Object.DestroyImmediate(clone);
                    }
                }
                if (Signature(candidate.Prefab, references) != original) throw new InvalidOperationException("Cached template changed: " + prefab.name);
                Object.DestroyImmediate(prefab);
            }
            if (parent.transform.childCount != 0) throw new InvalidOperationException("Clone cleanup incomplete");
            if (JsonUtility.ToJson(UnityEngine.Random.state) != randomBefore) throw new InvalidOperationException("Clone probe consumed Unity random state");
            Debug.Log($"[T3MPASYNCCLONE] VALIDATE PASS clones={checkedClones} randomUnchanged=True nativeOnly=True; hierarchy/components/transforms/shared-assets/static-colliders only, inert scripts removed from private sources, not gameplay lifecycle or whole-load speed");
        }
        finally
        {
            Object.DestroyImmediate(parent);
            UnityEngine.Random.state = savedRandom;
        }
    }

    private static bool Allowed(Component c) => c && (c.GetType() == typeof(Transform) || c.GetType() == typeof(MeshFilter) ||
        c.GetType() == typeof(MeshRenderer) || (c.GetType() == typeof(BoxCollider) && !((BoxCollider)c).isTrigger && !((BoxCollider)c).attachedRigidbody) ||
        // Reviewed native class contains only a serialized model-name string, no callbacks.
        (c.GetType().FullName == "Timberborn.Timbermesh.TimbermeshDescription" && c.GetType().Assembly.GetName().Name == "Timberborn.Timbermesh") ||
        // The staged mod is MVID-checked by the driver. Its sentinel callbacks do
        // nothing with SlotIndex == -1; this nonserialized field starts at -1.
        (c.GetType().FullName == "T3MP.ActiveInHierarchySentinel" && (int)c.GetType().GetField("SlotIndex", Fields)!.GetValue(c)! == -1));

    private static void CheckPlacement(GameObject clone, GameObject parent)
    {
        if (!clone || clone.activeInHierarchy || clone.transform.parent != parent.transform || clone.scene != parent.scene)
            throw new InvalidOperationException("Clone active, misplaced or missing");
    }

    private static string Signature(GameObject root, Dictionary<Object, int> references)
        => Signature(root, references, out _);

    private static string Signature(GameObject root, Dictionary<Object, int> references, out string snapshot)
    {
        var state = new StringBuilder();
        void F(float value) => state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        void V(Vector4 v) { F(v.x); F(v.y); F(v.z); F(v.w); }
        // Unity's equality compares native object identity on both supported versions.
        void Ref(Object value)
        {
            var id = 0;
            if (value && !references.TryGetValue(value, out id)) references.Add(value, id = references.Count + 1);
            state.Append(id).Append('|');
        }
        void Visit(Transform t)
        {
            var go = t.gameObject;
            state.Append(go.name.Length).Append(':').Append(go.name).Append('|').Append(go.activeSelf).Append('|')
                .Append(go.activeInHierarchy).Append('|').Append(go.layer).Append('|').Append(go.tag).Append('|').Append(go.isStatic).Append('|').Append(t.childCount).Append('|');
            V(t.localPosition); var q = t.localRotation; V(new Vector4(q.x, q.y, q.z, q.w)); V(t.localScale);
            foreach (var c in go.GetComponents<Component>())
            {
                if (!c) throw new InvalidOperationException("Missing cloned component on " + go.name);
                state.Append(c.GetType().FullName).Append('|');
                if (c is MeshFilter mesh) Ref(mesh.sharedMesh);
                else if (c is MeshRenderer renderer)
                {
                    state.Append(renderer.enabled).Append('|').Append(renderer.shadowCastingMode).Append('|').Append(renderer.receiveShadows).Append('|')
                        .Append(renderer.lightProbeUsage).Append('|').Append(renderer.reflectionProbeUsage).Append('|').Append(renderer.sortingLayerID).Append('|').Append(renderer.sortingOrder).Append('|');
                    foreach (var material in renderer.sharedMaterials) Ref(material);
                }
                else if (c is BoxCollider box)
                {
                    state.Append(box.enabled).Append('|').Append(box.isTrigger).Append('|');
                    V(box.center); V(box.size); F(box.contactOffset); Ref(box.sharedMaterial);
                }
                else if (c is MonoBehaviour)
                {
                    if (!Allowed(c)) throw new InvalidOperationException("Clone acquired unsafe script state");
                    state.Append(JsonUtility.ToJson(c)).Append('|');
                }
            }
            state.AppendLine();
            for (var i = 0; i < t.childCount; i++) Visit(t.GetChild(i));
        }
        Visit(root.transform);
        snapshot = state.ToString();
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot))).Replace("-", "");
    }
}
