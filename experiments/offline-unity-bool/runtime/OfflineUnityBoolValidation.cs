using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace T3MPTestDriver;

internal static class OfflineUnityBoolValidation
{
    internal static void RunIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        bool validate = args.Contains("-t3mpTestOfflineBoolValidate");
        if (!validate && !args.Contains("-t3mpTestBenchmarkTicks")) return;
        try
        {
            var type = typeof(UObject);
            var predicate = type.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static)!;
            var fileHash = Hash(File.ReadAllBytes(type.Assembly.Location));
            var bodyHash = Hash(predicate.GetMethodBody()!.GetILAsByteArray()!);
            Debug.Log("[T3MPTEST] Offline bool identity file=" + fileHash + " body=" + bodyHash);
            if (!validate) return;
            bool candidate = fileHash == "1BF09B56F0ABC61A14955C598F6E8EC9259A32AFB1A333F8B3BBD9943BA42098" &&
                bodyHash == "F127C3FB128390E7F8652AAA3927C27120D5097BAC54363BA5BA6E4F16A699E6";
            if (!candidate) throw new InvalidOperationException("Expected audited 1.1.2.4 candidate in memory and on disk");
            var compare = (Func<UObject?, UObject?, bool>)Delegate.CreateDelegate(typeof(Func<UObject?, UObject?, bool>),
                type.GetMethod("CompareBaseObjects", BindingFlags.NonPublic | BindingFlags.Static)!);
            int count = 0;
            void Check(string label, UObject? value, bool alive)
            {
                bool expected = !compare(value, null);
                bool actual = value!;
                if (expected != alive || actual != expected) throw new InvalidOperationException(label + " mismatched");
                ++count;
            }
            var owned = new List<UObject>();
            try
            {
                Check("managed-null", null, false);
                Check("managed-wrapper", new UObject(), false);
                var gameObject = new GameObject("T3MP offline bool lifetime probe"); owned.Add(gameObject);
                var transform = gameObject.transform;
                var texture = new Texture2D(2, 2); owned.Add(texture);
                var text = new TextAsset("probe"); owned.Add(text);
                Check("game-object-alive", gameObject, true);
                Check("transform-alive", transform, true);
                Check("texture-alive", texture, true);
                Check("text-alive", text, true);
                UObject.DestroyImmediate(gameObject);
                UObject.DestroyImmediate(texture);
                UObject.DestroyImmediate(text);
                Check("game-object-destroyed", gameObject, false);
                Check("transform-destroyed", transform, false);
                Check("texture-destroyed", texture, false);
                Check("text-destroyed", text, false);
            }
            finally
            {
                foreach (var item in owned) if (!compare(item, null)) UObject.DestroyImmediate(item);
            }
            Debug.Log("[T3MPTEST] Offline bool validation PASS cases=" + count + " file=" + fileHash + " body=" + bodyHash);
        }
        catch (Exception exception)
        {
            Debug.LogError("[T3MPTEST] Offline bool validation FAIL " + exception);
        }
    }

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }
}
