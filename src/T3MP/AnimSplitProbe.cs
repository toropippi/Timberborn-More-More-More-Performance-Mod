using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// TEMP ATTRIBUTION PROBE, arg-gated by '-benchAnim' (inert otherwise):
/// splits the per-frame Timbermesh animation sampling cost into
///   - VertexAnimationUpdater.UpdateAnimation (per animated mesh node: one
///     Material.SetFloat of _AnimationTime - GPU skinning, CPU only submits)
///   - NodeAnimationUpdater.UpdateAnimation   (per bone: keyframe lerp + a
///     native Transform write)
///   - AnimatorRegistry.UpdateSingleton       (the whole per-frame loop,
///     including the native isActiveAndEnabled checks and dispatch overhead)
/// so the 5.9 ms/frame AnimatorRegistry cost can be attributed to material
/// submission vs transform writes vs loop overhead. Measurement only - the
/// per-call stopwatch pair contaminates absolute frame time, so shares within
/// a run are meaningful but absolute ms are not.
/// </summary>
internal static class AnimSplitProbe
{
    public static readonly bool Enabled = BenchmarkSettings.BenchAnimRequested;

    private const double WindowSeconds = 20.0;

    private sealed class Slot
    {
        public long Calls;
        public long ElapsedTicks;
    }

    private static readonly Slot Vertex = new Slot();
    private static readonly Slot Node = new Slot();
    private static readonly Slot Loop = new Slot();
    private static readonly Stopwatch WindowClock = Stopwatch.StartNew();
    private static int _windowIndex;

    public static void RecordVertex(long elapsedStopwatchTicks)
    {
        Vertex.Calls++;
        Vertex.ElapsedTicks += elapsedStopwatchTicks;
    }

    public static void RecordNode(long elapsedStopwatchTicks)
    {
        Node.Calls++;
        Node.ElapsedTicks += elapsedStopwatchTicks;
    }

    // Called once per frame around the whole AnimatorRegistry loop; also the
    // report trigger (the loop runs every frame in the game scene).
    public static void RecordLoop(long elapsedStopwatchTicks)
    {
        Loop.Calls++;
        Loop.ElapsedTicks += elapsedStopwatchTicks;
        MaybeReport();
    }

    private static void MaybeReport()
    {
        if (WindowClock.Elapsed.TotalSeconds < WindowSeconds)
        {
            return;
        }

        var windowSeconds = WindowClock.Elapsed.TotalSeconds;
        WindowClock.Restart();
        _windowIndex++;

        var toMs = 1000.0 / Stopwatch.Frequency;
        var frames = Loop.Calls > 0 ? Loop.Calls : 1;
        var builder = new StringBuilder(1024);
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] AnimSplit window {0} ({1:0.0}s realtime), frames={2}",
            _windowIndex,
            windowSeconds,
            Loop.Calls);
        AppendSlot(builder, "loop(total)", Loop, toMs, frames);
        AppendSlot(builder, "vertexMaterialSet", Vertex, toMs, frames);
        AppendSlot(builder, "nodeTransformWrite", Node, toMs, frames);

        Debug.Log(builder.ToString());

        Vertex.Calls = 0; Vertex.ElapsedTicks = 0;
        Node.Calls = 0; Node.ElapsedTicks = 0;
        Loop.Calls = 0; Loop.ElapsedTicks = 0;
    }

    private static void AppendSlot(StringBuilder builder, string label, Slot slot, double toMs, long frames)
    {
        var totalMs = slot.ElapsedTicks * toMs;
        builder.AppendLine();
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] AnimSplit | {0} totalMs={1:0.0} msPerFrame={2:0.000} calls={3} callsPerFrame={4:0.0} avgUs={5:0.000}",
            label,
            totalMs,
            totalMs / frames,
            slot.Calls,
            (double)slot.Calls / frames,
            slot.Calls > 0 ? totalMs * 1000.0 / slot.Calls : 0.0);
    }
}
