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
    private static bool _direct3D11;
    private static readonly Dictionary<Type, RuntimePatches.Shape> Shapes = new Dictionary<Type, RuntimePatches.Shape>();
    private static bool _passThroughReported;
    private static Type? _harmonyType;
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    internal static long Calls, Uploaded, Reused;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        // Only the reviewed backend: other graphics devices keep the original transfer.
        _direct3D11 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
        if (!_direct3D11)
        {
            Debug.Log("[T3MP] Water upload de-duplication not active on " + SystemInfo.graphicsDeviceType + "; vanilla transfers retained.");
            return;
        }
        var open = typeof(DataTextureArray<>);
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            foreach (var (type, rewrite) in new[] {
                (typeof(float), nameof(RewriteFloat)), (typeof(Vector2), nameof(RewriteVector2)), (typeof(byte), nameof(RewriteByte)) })
            {
                var closed = open.MakeGenericType(type);
                var method = closed.GetMethod("UpdateTextureArrays", RuntimePatches.All | BindingFlags.DeclaredOnly, null, new[] { typeof(int) }, null)
                             ?? throw new MissingMethodException(closed.FullName, "UpdateTextureArrays");
                if (!RuntimePatches.ReviewedBody(method, ReviewedBody))
                    throw new InvalidOperationException("UpdateTextureArrays body is not a reviewed build for " + type.Name);
                if (method.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
                    throw new InvalidOperationException("UpdateTextureArrays has exception handling; not the reviewed build");
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, transpilersOnly: true))
                    throw new InvalidOperationException("another mod transpiles UpdateTextureArrays<" + type.Name + ">");
                Shapes[type] = RuntimePatches.OriginalShape(harmonyType, method);
                Targets.Add(method);
                apply(method, null, null, rewrite, null);
            }
        }, typeof(WaterTextureUpload));
        _harmonyType = harmonyType;
        if (Installed) Debug.Log("[T3MP] Water upload de-duplication installed (Direct3D11).");
    }

    // Called at every world load: a foreign transpiler added after this install
    // would otherwise be hidden by the body replacement. Restores vanilla.
    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null) return;
        try
        {
            if (!Targets.Any(t => RuntimePatches.ForeignPatched(_harmonyType, t, Owner, transpilersOnly: true))) return;
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
            Debug.Log("[T3MP] Water upload de-duplication removed: another mod now transpiles UpdateTextureArrays; vanilla restored.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Water upload revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // Raw-IL SHA256 of the reviewed vanilla body (scripts/IlFingerprint); the
    // generic definition owns the body, so it is identical for every T and is
    // the same on 1.0.13.1, 1.1.2.0 and 1.1.2.4.
    private const string ReviewedBody = "E64C910DE4741DCA47154585F217B4FB7C8383D07225DE8D8DF298C41A6E7980";

    private static IEnumerable<T> RewriteFloat<T>(IEnumerable<T> instructions) => Rewrite(instructions, typeof(float));
    private static IEnumerable<T> RewriteVector2<T>(IEnumerable<T> instructions) => Rewrite(instructions, typeof(Vector2));
    private static IEnumerable<T> RewriteByte<T>(IEnumerable<T> instructions) => Rewrite(instructions, typeof(byte));
    // Replaces the body only when Harmony hands over the vanilla stream; a
    // stream already changed by another mod's transpiler passes through.
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions, Type element)
    {
        // This runs only when Harmony rebuilds a patched method, including when
        // a foreign transpiler is added or removed. Native writes during a
        // pass-through interval invalidate the old GPU snapshot. Clear on both
        // transitions; do not inspect Harmony or allocate in the upload loop.
        States = new ConditionalWeakTable<Texture2DArray, State>();
        var list = new List<T>(instructions);
        if (!Shapes.TryGetValue(element, out var shape) || !RuntimePatches.SameShape(shape, RuntimePatches.DescribeShape(list)))
        {
            if (!_passThroughReported)
            {
                _passThroughReported = true;
                Debug.Log("[T3MP] Water upload: UpdateTextureArrays<" + element.Name + "> was changed by another mod; passing its instructions through unchanged.");
            }
            return list;
        }
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
        // Missing, destroyed or unexpected inputs take the vanilla statements so
        // vanilla raises its own exception; a cache hit never hides one.
        if (!_direct3D11 || target is null || !target || temporary is null || !temporary || !temporary.isReadable || data is null || data.Length == 0 ||
            (typeof(T) != typeof(byte) && typeof(T) != typeof(float) && typeof(T) != typeof(Vector2)))
        {
            Original(temporary!, target!, data!, layer);
            Uploaded++;
            return;
        }
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
