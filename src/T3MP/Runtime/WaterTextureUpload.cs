using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Timberborn.WaterSystemRendering;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// DataTextureArray<T>.UpdateTextureArrays uploads every column of the old and
// new water data arrays to the GPU on every water render update, whether or
// not the bytes changed. This helper remembers the bytes last submitted to each
// (texture, layer) and omits only a transfer whose bytes are identical to what
// that texture layer already holds. The simulation, the data arrays and the
// renderer are untouched; only redundant GPU transfers are skipped. The
// texture arrays are compute-shader inputs and have no other native writer than
// this method (audited on 1.0.13.1 and 1.1.2.4). Regenerating a Harmony wrapper
// discards snapshots because its native path may have written different bytes.
internal static class WaterTextureUpload
{
    private const string Owner = "t3mp.runtime.water";
    private sealed class State { public readonly Dictionary<int, byte[]> Layers = new Dictionary<int, byte[]>(); }
    private static ConditionalWeakTable<Texture2DArray, State> States = new ConditionalWeakTable<Texture2DArray, State>();
    internal static long Calls, Uploaded, Reused;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        var open = typeof(DataTextureArray<>);
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            foreach (var (type, rewrite) in new[] {
                (typeof(float), nameof(RewriteFloat)), (typeof(Vector2), nameof(RewriteVector2)), (typeof(byte), nameof(RewriteByte)) })
                apply(RuntimePatches.Find(open.MakeGenericType(type), "UpdateTextureArrays", new[] { typeof(int) }), null, null, rewrite, null);
        }, typeof(WaterTextureUpload));
        Installed = true;
        Debug.Log("[T3MP] Water upload de-duplication installed (" + SystemInfo.graphicsDeviceType + ").");
    }

    private static IEnumerable<T> RewriteFloat<T>(IEnumerable<T> instructions) => Rewrite<T>(typeof(float));
    private static IEnumerable<T> RewriteVector2<T>(IEnumerable<T> instructions) => Rewrite<T>(typeof(Vector2));
    private static IEnumerable<T> RewriteByte<T>(IEnumerable<T> instructions) => Rewrite<T>(typeof(byte));
    private static IEnumerable<T> Rewrite<T>(Type element)
    {
        // Harmony rebuilt the method: forget snapshots taken before.
        States = new ConditionalWeakTable<Texture2DArray, State>();
        return RuntimePatches.CallAndReturn<T>(typeof(WaterTextureUpload).GetMethod(nameof(Update), RuntimePatches.All)!.MakeGenericMethod(element), 2);
    }

    // Same statements as vanilla UpdateTextureArrays with the transfer routed through Upload.
    internal static void Update<T>(DataTextureArray<T> array, int columnIndex) where T : struct
    {
        Upload(array._bufferTexture, array.OldArray, array._oldData[columnIndex], columnIndex);
        Upload(array._bufferTexture, array.NewArray, array._newData[columnIndex], columnIndex);
    }

    private static void Upload<T>(Texture2D temporary, Texture2DArray target, T[] data, int layer) where T : struct
    {
        Calls++;
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(data));
        var state = States.GetOrCreateValue(target);
        state.Layers.TryGetValue(layer, out var previous);
        if (previous != null && EqualBytes(bytes, previous))
        {
            Reused++;
            return;
        }
        Original(temporary, target, data, layer);
        // Snapshot only after the original transfer returned without throwing.
        if (previous == null || previous.Length != bytes.Length)
        {
            previous = new byte[bytes.Length];
            state.Layers[layer] = previous;
        }
        CopyBytes(bytes, previous);
        Uploaded++;
    }

    // Native memcmp/memcpy: the managed Span helpers are not vectorized on the
    // game's Mono and cost tens of milliseconds per tick on large maps.
    private static unsafe bool EqualBytes(ReadOnlySpan<byte> current, byte[] previous)
    {
        if (current.Length != previous.Length) return false;
        if (current.Length == 0) return true;
        fixed (byte* left = current)
        fixed (byte* right = previous)
            return UnsafeUtility.MemCmp(left, right, current.Length) == 0;
    }

    private static unsafe void CopyBytes(ReadOnlySpan<byte> source, byte[] destination)
    {
        if (source.Length == 0) return;
        fixed (byte* from = source)
        fixed (byte* to = destination)
            UnsafeUtility.MemCpy(to, from, source.Length);
    }

    private static void Original<T>(Texture2D temporary, Texture2DArray target, T[] data, int layer) where T : struct
    {
        temporary.SetPixelData(data, 0);
        temporary.Apply(false, false);
        Graphics.CopyTexture(temporary, 0, 0, target, layer, 0);
    }

}
