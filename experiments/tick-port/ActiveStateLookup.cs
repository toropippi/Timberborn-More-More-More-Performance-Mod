using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace T3MP.Runtime;

// Port of the old notification-based activeInHierarchy lookup, initially run
// in comparison mode. Return the native value while checking notification
// ordering; this must pass lifecycle tests before it can drive simulation.
internal static class ActiveStateLookup
{
    internal sealed class State
    {
        internal GameObject Object = null!;
        internal bool Known;
        internal bool Value;
    }
    private static readonly ConditionalWeakTable<GameObject, State> States = new();
    internal static long Reads { get; private set; }
    internal static long Mismatches { get; private set; }
    internal static State Attach(GameObject gameObject)
    {
        if (States.TryGetValue(gameObject, out var existing)) return existing;
        var state = new State { Object = gameObject };
        States.Add(gameObject, state);
        var observer = gameObject.AddComponent<ActiveStateObserver>();
        observer.State = state;
        state.Value = gameObject.activeInHierarchy;
        state.Known = true;
        return state;
    }
    internal static bool Read(GameObject gameObject)
    {
        var native = gameObject.activeInHierarchy;
        Reads++;
        if (States.TryGetValue(gameObject, out var state) && state.Known && state.Value != native)
            Mismatches++;
        return native;
    }
}

internal sealed class ActiveStateObserver : MonoBehaviour
{
    [NonSerialized] internal ActiveStateLookup.State? State;
    private void OnEnable() => Publish();
    private void OnDisable() => Publish();
    private void Publish()
    {
        if (State == null) return;
        State.Value = gameObject.activeInHierarchy;
        // Disabling this observer does not disable the GameObject. Fall back
        // until it receives OnEnable, rather than leaving an unmaintained bit.
        State.Known = enabled;
    }
    private void OnDestroy()
    {
        if (State != null) State.Known = false;
        State = null;
    }
}
