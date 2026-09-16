using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    internal static class Debug
    {
        internal static readonly List<string> Messages = new();
        public static void Log(object value) => Messages.Add(value.ToString()!);
        public static void LogWarning(object value) => Log(value);
        public static void LogError(object value) => Log(value);
    }
}

namespace Timberborn.WalkingSystem
{
    public class WalkerSpeedManager
    {
        public float Value;
        public int Calls;
        public bool Throw;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public float GetWalkerSpeedAtCurrentPosition()
        {
            Calls++;
            if (Throw) throw new ApplicationException("speed failure");
            return Value + Calls;
        }
    }
    public class Enterer { public bool IsInside; public int Exits; public void Exit() { Exits++; IsInside = false; } }
    public class Walker { public CharacterMovementSystem.PathFollower PathFollower = new(); }
    public class WalkerMover
    {
        public Enterer _enterer = new();
        public Walker _walker = new();
        public WalkerSpeedManager _walkerSpeedManager = new();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Move()
        {
            if (_enterer.IsInside) _enterer.Exit();
            else _walker.PathFollower.MoveAlongPath(0.1f, "Walking", _walkerSpeedManager.GetWalkerSpeedAtCurrentPosition);
        }
    }
}

namespace Timberborn.CharacterMovementSystem
{
    public class PathFollower
    {
        // Observations for the test; reviewed native consumers do not retain
        // or compare the delegate. They only forward and invoke it.
        public Func<float>? LastProvider;
        public readonly List<float> Speeds = new();
        public int Moves;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void MoveAlongPath(float delta, string animation, Func<float> provider)
        {
            Moves++; LastProvider = provider;
            Speeds.Add(GetSpeedLimitIfCloseToTarget(delta, provider) ?? 0);
            Speeds.Add(GetMovementSpeed(provider));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public float? GetSpeedLimitIfCloseToTarget(float delta, Func<float> provider) => GetMovementSpeed(provider);
        [MethodImpl(MethodImplOptions.NoInlining)]
        public float GetMovementSpeed(Func<float> provider) => provider();
    }
}

// Unrelated features are not installed in this executable. Production patch
// installation/IL/foreign-owner helpers and the feature under test are linked.
namespace T3MP.Runtime
{
    internal static class RequestedSpeedPolicy { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class EventBusFastDelegates { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class WaterTextureUpload { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class TickFrontier { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class TubeVisitFix { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class TerrainNeighborVisits { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class AllowedGoodRows { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class YielderReachabilitySkip { internal static void Install(Type a, Type b, MethodInfo c) { } }
}
