using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace T3MPTestDriver;

// Dev-only GPU regression. A foreign transpiler temporarily restores the native
// writer, then is removed in the same process while the textures stay alive.
internal static class WaterUploadValidation
{
    private const string Owner = "t3mp.test.water-upload";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestWaterUpload", StringComparer.OrdinalIgnoreCase);

    internal static void Run()
    {
        try
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || !SystemInfo.supportsAsyncGPUReadback)
                throw new InvalidOperationException("Water regression requires D3D11 GPU readback");
            Check(TextureFormat.RFloat, 0.25f, 0.75f);
            Check(TextureFormat.RGFloat, new Vector2(0.25f, 0.5f), new Vector2(0.75f, 1f));
            Check(TextureFormat.R8, (byte)31, (byte)213);
            Debug.Log("[T3MPTEST] Water upload validation PASS: float/Vector2/byte GPU A, identical reuse, foreign native B, patch removal A, resumed reuse.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[T3MPTEST] Water upload validation FAIL: " + exception);
        }
    }

    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    private static void Check<T>(TextureFormat format, T a, T b) where T : struct
    {
        var feature = Find("T3MP.Runtime.WaterTextureUpload");
        if (!(bool)feature.GetProperty("Installed", All)!.GetValue(null)!)
            throw new InvalidOperationException("Water regression requires the active T3MP rewrite");
        long Count(string name) => (long)feature.GetField(name, All)!.GetValue(null)!;
        var arrayType = Find("Timberborn.WaterSystemRendering.DataTextureArray`1").MakeGenericType(typeof(T));
        var array = arrayType.GetMethod("Create", All)!.Invoke(null, new object[] { format, new Vector2Int(2, 2) })!;
        var update = arrayType.GetMethod("UpdateTextureArrays", All)!;
        var oldData = ((T[][])arrayType.GetProperty("OldData", All)!.GetValue(array)!)[0];
        var newData = ((T[][])arrayType.GetProperty("NewData", All)!.GetValue(array)!)[0];
        var oldTexture = (Texture2DArray)arrayType.GetProperty("OldArray", All)!.GetValue(array)!;
        var newTexture = (Texture2DArray)arrayType.GetProperty("NewArray", All)!.GetValue(array)!;
        var harmonyType = Find("HarmonyLib.Harmony");
        var harmony = Activator.CreateInstance(harmonyType, Owner)!;
        void Unpatch() => harmonyType.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
        void Write(T value, string stage)
        {
            for (var i = 0; i < oldData.Length; i++) oldData[i] = newData[i] = value;
            update.Invoke(array, new object[] { 0 });
            ReadGpu(oldTexture, oldData, stage + " old");
            ReadGpu(newTexture, newData, stage + " new");
        }
        try
        {
            var uploads = Count("Uploaded");
            Write(a, "initial A");
            Require(Count("Uploaded") == uploads + 2, "Initial A did not upload both layers");
            var reused = Count("Reused");
            Write(a, "repeated A");
            Require(Count("Uploaded") == uploads + 2 && Count("Reused") == reused + 2, "Identical A was not reused");

            var foreign = T3MP.Loading.LoadPatchBridge.Create(Owner, typeof(WaterUploadValidation).GetMethod(nameof(ForeignBody), All)!);
            var hook = Activator.CreateInstance(Find("HarmonyLib.HarmonyMethod"), foreign)!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            patch.Invoke(harmony, new object?[] { update, null, null, hook, null });
            var calls = Count("Calls");
            Write(b, "foreign B");
            Require(Count("Calls") == calls, "Foreign body did not use the native writer");

            Unpatch();
            Write(a, "restored A");
            Require(Count("Uploaded") == uploads + 4, "Restored helper reused a stale snapshot");
            reused = Count("Reused");
            Write(a, "repeated restored A");
            Require(Count("Uploaded") == uploads + 4 && Count("Reused") == reused + 2, "Reuse did not resume after restoration");
            Debug.Log("[T3MPTEST] Water GPU sequence PASS: " + typeof(T).Name);
        }
        finally
        {
            try { Unpatch(); }
            finally { arrayType.GetMethod("Cleanup", All)!.Invoke(array, null); }
        }
    }

    private static void ReadGpu<T>(Texture2DArray texture, T[] expected, string stage) where T : struct
    {
        var request = AsyncGPUReadback.Request(texture, 0);
        request.WaitForCompletion();
        Require(!request.hasError && request.done, "GPU readback failed");
        var actual = request.GetData<byte>();
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(expected));
        Require(actual.Length == bytes.Length, "GPU byte count differs");
        for (var i = 0; i < bytes.Length; i++)
            Require(actual[i] == bytes[i], stage + ": GPU bytes differ at " + i + " for " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> ForeignBody<T>(IEnumerable<T> instructions)
    {
        yield return (T)Activator.CreateInstance(typeof(T), OpCodes.Nop, null)!;
        foreach (var instruction in instructions) yield return instruction;
    }
}
