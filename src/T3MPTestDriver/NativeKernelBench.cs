using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Bindito.Core;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.NeedBehaviorSystem;
using Timberborn.NeedSystem;
using Timberborn.TimeSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only microbenchmark (2026-09-17): the travel-distance query behind
// DistrictNeedBehaviorService.PickShortestAction, on the live save's real
// characters, candidate buildings and cached flow fields, measured three ways in
// one process while the game is paused:
//   A  the game's own managed path (NavigationService.FindPathUnlimitedRange)
//   B  flat open-addressing mirrors of the cached flow fields, C# (Mono JIT)
//   C  the same mirrors read by native code (experiments/native-kernel/kernel.c)
// B and C answer only what the game answers from its caches (FindPathUncached
// steps 1-4); every other leg goes back to A and is counted. Results are compared
// bit for bit with A. Enabled by -t3mpTestNativeKernelBench <path to t3mp_native.dll>.
internal sealed class NativeKernelBench : MonoBehaviour
{
    private const string Flag = "-t3mpTestNativeKernelBench";
    private const int WarmTicks = 150, Rounds = 7, TargetPairs = 40000;
    private IContainer _container = null!;
    private EntityRegistry _registry = null!;
    private SpeedManager _speed = null!;
    private long _startTicks = -1;
    private bool _paused, _done;

    internal static bool Requested => Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase)) >= 0;
    private static string DllPath
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        }
    }

    internal void Initialize(IContainer container, EntityRegistry registry, SpeedManager speed)
    {
        _container = container; _registry = registry; _speed = speed;
    }

    private void Update()
    {
        if (_done) return;
        if (_startTicks < 0) _startTicks = FullTickCounter.FullTicks;
        if (FullTickCounter.FullTicks < _startTicks + WarmTicks) return;
        if (!_paused) { _speed.ChangeSpeed(0f); _paused = true; return; }
        _done = true;
        try { Run(); }
        catch (Exception exception) { Debug.LogError("[T3MPNATIVE] failed: " + exception); }
    }

    private sealed class Mirror
    {
        internal int[] DirKeys = null!, DirVals = null!, FieldOffset = null!, FieldMask = null!, Keys = null!;
        internal byte[] FieldFilled = null!;
        internal float[] Vals = null!;
        internal int DirMask, Fields, Filled;
        internal long Entries;
    }

    private static int Slot(int key, int mask) => (int)((((uint)key * 0x9E3779B1u) >> 8) & (uint)mask);
    private static int Capacity(int count) { var c = 4; while (c < count * 2) c <<= 1; return c; }

    private static Mirror Build(FlowFieldCache cache)
    {
        var source = cache._flowFields;
        var mirror = new Mirror { Fields = source.Count };
        var dirCapacity = Capacity(source.Count);
        mirror.DirMask = dirCapacity - 1;
        mirror.DirKeys = Enumerable.Repeat(-1, dirCapacity).ToArray();
        mirror.DirVals = new int[dirCapacity];
        mirror.FieldOffset = new int[source.Count];
        mirror.FieldMask = new int[source.Count];
        mirror.FieldFilled = new byte[source.Count];
        long arena = 0;
        foreach (var pair in source) arena += Capacity(pair.Value.AccessFlowField._nodes.Count);
        if (arena > int.MaxValue) throw new InvalidOperationException("flow field arena too large: " + arena);
        mirror.Keys = new int[arena];
        mirror.Vals = new float[arena];
        for (long i = 0; i < arena; i++) mirror.Keys[i] = -1;
        int index = 0, offset = 0;
        foreach (var pair in source)
        {
            var field = pair.Value.AccessFlowField;
            var slot = Slot(pair.Key, mirror.DirMask);
            while (mirror.DirKeys[slot] != -1) slot = (slot + 1) & mirror.DirMask;
            mirror.DirKeys[slot] = pair.Key; mirror.DirVals[slot] = index;
            var capacity = Capacity(field._nodes.Count);
            mirror.FieldOffset[index] = offset; mirror.FieldMask[index] = capacity - 1;
            mirror.FieldFilled[index] = (byte)(field.IsFilled ? 1 : 0);
            if (field.IsFilled) mirror.Filled++;
            foreach (var node in field._nodes)
            {
                var j = Slot(node.Key, capacity - 1);
                while (mirror.Keys[offset + j] != -1) j = (j + 1) & (capacity - 1);
                mirror.Keys[offset + j] = node.Key; mirror.Vals[offset + j] = node.Value.Distance;
            }
            mirror.Entries += field._nodes.Count;
            offset += capacity; index++;
        }
        return mirror;
    }

    // 0 = miss, 1 = hit, 2 = field exists but is not filled (same as kernel.c).
    private static int Lookup(Mirror c, int fieldNode, int node, out float distance)
    {
        distance = 0f;
        var i = Slot(fieldNode, c.DirMask);
        int field;
        for (;;)
        {
            var k = c.DirKeys[i];
            if (k == fieldNode) { field = c.DirVals[i]; break; }
            if (k == -1) return 0;
            i = (i + 1) & c.DirMask;
        }
        if (c.FieldFilled[field] == 0) return 2;
        var mask = c.FieldMask[field];
        var offset = c.FieldOffset[field];
        var j = Slot(node, mask);
        for (;;)
        {
            var k = c.Keys[offset + j];
            if (k == node) { distance = c.Vals[offset + j]; return 1; }
            if (k == -1) return 0;
            j = (j + 1) & mask;
        }
    }

    private static bool Query(Mirror road, Mirror terrain, int s, int d, out float distance)
    {
        var r = Lookup(road, s, d, out distance);
        if (r == 1) return true;
        if (r == 2) return false;
        r = Lookup(road, d, s, out distance);
        if (r == 1) return true;
        if (r == 2) return false;
        if (Lookup(terrain, s, d, out distance) == 1) return true;
        return Lookup(terrain, d, s, out distance) == 1;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("t3mp_native")] private static extern void t3mp_init(int which, IntPtr dirKeys, IntPtr dirVals, int dirMask,
        IntPtr fieldOffset, IntPtr fieldMask, IntPtr fieldFilled, IntPtr keys, IntPtr vals, int sizeY, int sizeZ);
    [DllImport("t3mp_native")] private static extern int t3mp_batch(float sx, float sy, float sz, IntPtr candidates, int count,
        IntPtr leg1, IntPtr leg2, IntPtr flags);

    private void Run()
    {
        var culture = CultureInfo.InvariantCulture;
        var nav = (NavigationService)_container.GetInstance<INavigationService>();
        var pathfinding = nav._pathfindingService;
        var ids = pathfinding._nodeIdService;
        int sizeY = ids._size.y, sizeZ = ids._size.z;

        // Workload: sampled characters x every need behavior of the largest district that offers a position.
        var entities = _registry.Entities.ToList();
        var services = entities.Select(e => e.GetComponent<DistrictNeedBehaviorService>()).Where(s => s != null).ToList();
        if (services.Count == 0) throw new InvalidOperationException("no DistrictNeedBehaviorService");
        var behaviors = services.Select(s => s._needBehaviors.Values.SelectMany(g => g.NeedBehaviors).ToList()).OrderByDescending(l => l.Count).First();
        var characters = entities.Select(e => e.GetComponent<NeedManager>()).Where(n => n != null).ToList();
        var stride = Math.Max(1, (int)((long)characters.Count * behaviors.Count / TargetPairs));
        var starts = new List<Vector3>(); var first = new List<int>(); var counts = new List<int>(); var candidates = new List<float>();
        for (var c = 0; c < characters.Count; c += stride)
        {
            var needManager = characters[c];
            var begin = candidates.Count / 3;
            foreach (var behavior in behaviors)
            {
                Vector3? position;
                try { position = behavior.ActionPosition(needManager); } catch { continue; }
                if (position == null) continue;
                var p = position.GetValueOrDefault();
                candidates.Add(p.x); candidates.Add(p.y); candidates.Add(p.z);
            }
            var n = candidates.Count / 3 - begin;
            if (n == 0) continue;
            starts.Add(needManager.Transform.position); first.Add(begin); counts.Add(n);
        }
        var cand = candidates.ToArray();
        var pairs = cand.Length / 3;
        Debug.Log(string.Format(culture, "[T3MPNATIVE] workload characters={0}/{1} behaviors={2} pairs={3} legs={4} stride={5}",
            starts.Count, characters.Count, behaviors.Count, pairs, pairs * 2, stride));

        float[] refLeg1 = new float[pairs], refLeg2 = new float[pairs], leg1 = new float[pairs], leg2 = new float[pairs];
        bool[] refFound1 = new bool[pairs], refFound2 = new bool[pairs];
        var flags = new byte[pairs];

        var resolved = new byte[pairs];   // bit0/bit1: the mirror answers leg1/leg2
        long PassA(bool record, int mode)
        {
            var watch = Stopwatch.StartNew();
            for (var c = 0; c < starts.Count; c++)
            {
                var s = starts[c];
                for (int i = first[c], end = first[c] + counts[c]; i < end; i++)
                {
                    var d = new Vector3(cand[3 * i], cand[3 * i + 1], cand[3 * i + 2]);
                    var r = resolved[i];
                    if (mode == 0 || (mode == 1) == ((r & 1) != 0))
                    {
                        var f1 = nav.FindPathUnlimitedRange(s, d, null, out var d1);
                        if (record) { refFound1[i] = f1; refLeg1[i] = d1; }
                    }
                    if (mode == 0 || (mode == 1) == ((r & 2) != 0))
                    {
                        var f2 = nav.FindPathUnlimitedRange(d, s, null, out var d2);
                        if (record) { refFound2[i] = f2; refLeg2[i] = d2; }
                    }
                }
            }
            return watch.ElapsedTicks;
        }

        var warm = PassA(false, 0);      // fills what the game can fill
        var reference = PassA(true, 0);  // reference results on the warm caches
        Debug.Log(string.Format(culture, "[T3MPNATIVE] full managed passes: first={0:F0} ms second={1:F0} ms", warm * 1000.0 / Stopwatch.Frequency, reference * 1000.0 / Stopwatch.Frequency));
        var buildWatch = Stopwatch.StartNew();
        var road = Build(pathfinding._roadFlowFieldCache._flowFields);
        var terrain = Build(pathfinding._terrainFlowFieldCache._flowFields);
        var buildMs = buildWatch.Elapsed.TotalMilliseconds;
        var mirrorMb = ((long)road.Keys.Length + terrain.Keys.Length) * 8 / 1048576.0;
        Debug.Log(string.Format(culture, "[T3MPNATIVE] mirror road fields={0} filled={1} entries={2} terrain fields={3} filled={4} entries={5} memoryMB={6:F1} buildMs={7:F1}",
            road.Fields, road.Filled, road.Entries, terrain.Fields, terrain.Filled, terrain.Entries, mirrorMb, buildMs));

        int NodeId(float x, float y, float z) =>
            ((int)Mathf.Floor(x) + 1) * sizeY * sizeZ + ((int)Mathf.Floor(z) + 1) * sizeZ + ((int)Mathf.Floor(y + 0.1f) + 1);

        long resolvedLegs = 0;
        for (var c = 0; c < starts.Count; c++)
        {
            var sv = starts[c];
            var s = NodeId(sv.x, sv.y, sv.z);
            for (int i = first[c], end = first[c] + counts[c]; i < end; i++)
            {
                var d = NodeId(cand[3 * i], cand[3 * i + 1], cand[3 * i + 2]);
                byte r = 0;
                if (Query(road, terrain, s, d, out _)) { r |= 1; resolvedLegs++; }
                if (Query(road, terrain, d, s, out _)) { r |= 2; resolvedLegs++; }
                resolved[i] = r;
            }
        }
        var unresolvedTicks = PassA(false, 2);
        Debug.Log(string.Format(culture, "[T3MPNATIVE] legs={0} answeredFromCaches={1} ({2:P1}) others={3}; managed time on the others={4:F0} ms ({5:F1} us each)",
            pairs * 2, resolvedLegs, resolvedLegs / (double)(pairs * 2), pairs * 2 - resolvedLegs,
            unresolvedTicks * 1000.0 / Stopwatch.Frequency, unresolvedTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, pairs * 2 - resolvedLegs)));

        long unresolvedB = 0, unresolvedC = 0;
        long PassB()
        {
            long unresolved = 0;
            var watch = Stopwatch.StartNew();
            for (var c = 0; c < starts.Count; c++)
            {
                var sv = starts[c];
                var s = NodeId(sv.x, sv.y, sv.z);
                for (int i = first[c], end = first[c] + counts[c]; i < end; i++)
                {
                    var d = NodeId(cand[3 * i], cand[3 * i + 1], cand[3 * i + 2]);
                    byte f = 0;
                    if (Query(road, terrain, s, d, out leg1[i])) f |= 1; else unresolved++;
                    if (Query(road, terrain, d, s, out leg2[i])) f |= 2; else unresolved++;
                    flags[i] = f;
                }
            }
            unresolvedB = unresolved;
            return watch.ElapsedTicks;
        }

        long Mismatches()
        {
            long bad = 0;
            for (var i = 0; i < pairs; i++)
            {
                var f = flags[i];
                if ((f & 1) != 0 && (!refFound1[i] || BitConverter.SingleToInt32Bits(leg1[i]) != BitConverter.SingleToInt32Bits(refLeg1[i]))) bad++;
                if ((f & 2) != 0 && (!refFound2[i] || BitConverter.SingleToInt32Bits(leg2[i]) != BitConverter.SingleToInt32Bits(refLeg2[i]))) bad++;
            }
            return bad;
        }

        var native = LoadLibraryW(DllPath) != IntPtr.Zero;
        var handles = new List<GCHandle>();
        IntPtr Pin(Array array) { var h = GCHandle.Alloc(array, GCHandleType.Pinned); handles.Add(h); return h.AddrOfPinnedObject(); }
        IntPtr candPtr = IntPtr.Zero, leg1Ptr = IntPtr.Zero, leg2Ptr = IntPtr.Zero, flagsPtr = IntPtr.Zero;
        if (native)
        {
            foreach (var (which, m) in new[] { (0, road), (1, terrain) })
                t3mp_init(which, Pin(m.DirKeys), Pin(m.DirVals), m.DirMask, Pin(m.FieldOffset), Pin(m.FieldMask), Pin(m.FieldFilled), Pin(m.Keys), Pin(m.Vals), sizeY, sizeZ);
            candPtr = Pin(cand); leg1Ptr = Pin(leg1); leg2Ptr = Pin(leg2); flagsPtr = Pin(flags);
        }
        else Debug.LogWarning("[T3MPNATIVE] native library not loaded: " + DllPath + " error=" + Marshal.GetLastWin32Error());

        long PassC()
        {
            long unresolved = 0;
            var watch = Stopwatch.StartNew();
            for (var c = 0; c < starts.Count; c++)
            {
                var sv = starts[c];
                var begin = first[c];
                var missing = t3mp_batch(sv.x, sv.y, sv.z, IntPtr.Add(candPtr, begin * 12), counts[c],
                    IntPtr.Add(leg1Ptr, begin * 4), IntPtr.Add(leg2Ptr, begin * 4), IntPtr.Add(flagsPtr, begin));
                unresolved += missing;
            }
            unresolvedC = unresolved;
            return watch.ElapsedTicks;
        }

        var a = new List<long>(); var b = new List<long>(); var cc = new List<long>();
        long mismatchB = -1, mismatchC = -1;
        for (var round = 0; round < Rounds; round++)
        {
            a.Add(PassA(false, 1));
            b.Add(PassB()); if (round == 0) mismatchB = Mismatches();
            if (native) { cc.Add(PassC()); if (round == 0) mismatchC = Mismatches(); }
        }
        foreach (var handle in handles) handle.Free();

        var found = refFound1.Count(x => x) + refFound2.Count(x => x);
        Debug.Log(string.Format(culture, "[T3MPNATIVE] reference legs={0} found={1} ({2:P1}) unresolvedByMirror B={3} ({4:P2}) C={5} mismatches B={6} C={7}",
            pairs * 2, found, found / (double)(pairs * 2), unresolvedB, unresolvedB / (double)(pairs * 2), unresolvedC, mismatchB, mismatchC));
        void Report(string name, List<long> ticks, double baseline)
        {
            if (ticks.Count == 0) return;
            var ms = ticks.Select(t => t * 1000.0 / Stopwatch.Frequency).OrderBy(x => x).ToList();
            double min = ms[0], median = ms[ms.Count / 2];
            Debug.Log(string.Format(culture, "[T3MPNATIVE] {0} minMs={1:F2} medianMs={2:F2} nsPerLeg={3:F1} speedupVsA={4:F2}",
                name, min, median, min * 1e6 / Math.Max(1, resolvedLegs), baseline / min));
        }
        var baseA = a.Min() * 1000.0 / Stopwatch.Frequency;
        Report("A game-managed (cache-answered legs only)", a, baseA);
        Report("B flat-tables-mono", b, baseA);
        Report("C flat-tables-native", cc, baseA);
        Debug.Log("[T3MPNATIVE] done");
    }
}
