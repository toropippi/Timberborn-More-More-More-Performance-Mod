using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace T3MPTestDriver;

// Isolated Unity objects, no settlement entities or simulation decisions.
internal static class ActiveLookupValidation
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestActiveLookupAudit");
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    internal static void Run()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.ActiveStateLookup")).First(t => t != null)!;
        var attach = type.GetMethod("Attach", All)!;
        var read = (Func<GameObject, bool>)type.GetMethod("Read", All)!.CreateDelegate(typeof(Func<GameObject, bool>));
        long Mismatches() => (long)type.GetProperty("Mismatches", All)!.GetValue(null)!;
        for (var order = 0; order < 2; order++)
        {
            var parent = new GameObject("T3MPTEST.ActiveLookup.Parent");
            var child = new GameObject("T3MPTEST.ActiveLookup.Child");
            child.transform.SetParent(parent.transform);
            ActiveLookupCallback callback;
            if (order == 0)
            {
                callback = child.AddComponent<ActiveLookupCallback>();
                attach.Invoke(null, new object[] { child });
            }
            else
            {
                attach.Invoke(null, new object[] { child });
                callback = child.AddComponent<ActiveLookupCallback>();
            }
            callback.Read = read;
            var before = Mismatches();
            child.SetActive(false); child.SetActive(true);
            parent.SetActive(false); parent.SetActive(true);
            var delta = Mismatches() - before;
            Debug.Log($"[T3MPTEST] ActiveLookup ordering={order}, callbacks={callback.Calls}, cachedStateMismatches={delta}, nativeReturnsCorrect={callback.Correct}");
            callback.Read = null;
            UnityEngine.Object.DestroyImmediate(parent);
        }
    }
}

public sealed class ActiveLookupCallback : MonoBehaviour
{
    internal Func<GameObject, bool>? Read;
    internal int Calls;
    internal bool Correct = true;
    private void OnEnable() => Observe();
    private void OnDisable() => Observe();
    private void Observe()
    {
        if (Read == null) return;
        Calls++;
        Correct &= Read(gameObject) == gameObject.activeInHierarchy;
    }
}
