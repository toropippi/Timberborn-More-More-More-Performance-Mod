using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.PrefabOptimization;
using Timberborn.Common;
using UnityEngine;

namespace T3MPTestDriver;

internal static class MeshBuilderCapacityExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [ThreadStatic] private static int _depth;
    private static int _builders;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestMeshCapacity")) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.mesh-capacity");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(MeshBuilderCapacityExperiment).GetMethod(name, All))!;
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        foreach (var constructor in typeof(MeshBuilder).GetConstructors(All))
            if (!constructor.IsStatic) patch.Invoke(harmony, new object?[] { constructor, null, Hook(nameof(Created)), null, null });
        Debug.Log("[T3MPMESHCAPACITY] installed; load-only seed capacity=1, original growth retained");
    }
    private static void Begin() { if (_depth++ == 0) _builders = 0; }
    private static void End()
    {
        if (--_depth == 0) Debug.Log("[T3MPMESHCAPACITY] builders=" + _builders);
    }
    private static void Created(MeshBuilder __instance)
    {
        if (_depth == 0 || __instance._vertexCapacity != 0 || __instance._vertices != null) return;
        // A real one-element buffer selects the game's existing geometric growth
        // branch. The first nontrivial append then allocates max(2, needed), instead
        // of at least 6000 entries in every present vertex channel.
        Seed(__instance);
        _builders++;
    }
    private static void Seed(MeshBuilder builder)
    { builder._vertexCapacity = 1; builder._vertices = new Vector3[1]; }

    internal static void Validate()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestMeshCapacityValidate")) return;
        if (_depth != 0) throw new Exception("Mesh capacity validation must run outside load");
        var original = new MeshBuilder("capacity-test"); var compact = new MeshBuilder("capacity-test"); Seed(compact);
        var random = new System.Random(18839);
        var sizes = new[] { 0, 1, 2, 3, 63, 127, 257, 5999, 6000, 6001, 12017 };
        var checks = 0;
        for (var trial = 0; trial < 32; trial++)
        {
            original.Reset("capacity-test"); compact.Reset("capacity-test");
            for (var append = 0; append < 5; append++)
            {
                var n = sizes[random.Next(sizes.Length)];
                Vector3[] V3(bool enabled)
                {
                    var a = new Vector3[enabled ? n : 0];
                    for (var i = 0; i < a.Length; i++) a[i] = new Vector3(i * .125f, (i % 13) * -.25f, i % 7);
                    return a;
                }
                Vector4[] V4(bool enabled)
                {
                    var a = new Vector4[enabled ? n : 0];
                    for (var i = 0; i < a.Length; i++) a[i] = new Vector4(i * .125f, (i % 13) * -.25f, i % 7, 1);
                    return a;
                }
                var colors = new Color32[random.Next(2) == 0 ? n : 0];
                for (var i = 0; i < colors.Length; i++) colors[i] = new Color32((byte)i, (byte)(i >> 3), 51, 255);
                var indices = Enumerable.Range(0, n / 3 * 3).ToArray();
                var input = new IntermediateMesh { VertexCount = n, Vertices = V3(true), Normals = V3(random.Next(2) == 0),
                    Tangents = V4(random.Next(2) == 0), Colors = colors, UV0 = V4(random.Next(2) == 0),
                    UV1 = V4(random.Next(2) == 0), UV2 = V4(random.Next(2) == 0),
                    Submeshes = new[] { (new NullableKey<Material>(null!), indices) } };
                var transform = new Matrix4x4Transform(Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(0, trial * 7, 0), new Vector3(1, 2, 1)));
                original.AppendIntermediateMesh(input, transform); compact.AppendIntermediateMesh(input, transform);
            }
            var a = original.Build(); var b = compact.Build();
            try
            {
                if (MeshSnapshot.Digest(a.Mesh) != MeshSnapshot.Digest(b.Mesh) || !a.Materials.SequenceEqual(b.Materials))
                    throw new Exception("Mesh capacity output differs: " + trial);
                checks++;
            }
            finally { UnityEngine.Object.DestroyImmediate(a.Mesh); UnityEngine.Object.DestroyImmediate(b.Mesh); }
        }
        Debug.Log("[T3MPMESHCAPACITY] VALIDATE PASS cases=" + checks + " (growth boundaries, reset, missing channels, transforms, exact geometry)");
    }
}
