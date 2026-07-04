using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace T3MP;

/// <summary>
/// Skips applying Timbermesh animation POSES (bone transforms / vertex
/// animation material updates) for animators whose renderers are not visible
/// to any camera. Visually lossless: Unity's Renderer.isVisible includes
/// shadow rendering, so anything that could appear on screen keeps animating.
/// Animation TIME still advances normally (TimbermeshAnimator.UpdateAnimation
/// keeps running), so gameplay-visible state (PlayingFinished, Time,
/// AnimationChanged) is untouched - only the per-frame pose write is skipped.
///
/// Stale-pose safety: when a pose update is skipped the animator is marked
/// dirty; the next update while visible applies the current pose. For
/// one-shot animations that FINISH while off-screen (UpdateAnimation stops
/// calling the pose update), the UpdateAnimation hook applies the final pose
/// once the renderer becomes visible again.
/// </summary>
internal static class InvisibleAnimatorPoseSkip
{
    private sealed class State
    {
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public bool PoseDirty;
    }

    private static readonly ConditionalWeakTable<Component, State> States = new ConditionalWeakTable<Component, State>();
    private static readonly ConditionalWeakTable<Component, State>.CreateValueCallback CreateState = CreateStateFor;
    private static MethodInfo? _updateAnimationUpdatersMethod;
    private static PropertyInfo? _playingFinishedProperty;

    private static State CreateStateFor(Component animator)
    {
        var state = new State();
        try
        {
            state.Renderers = animator.GetComponentsInChildren<Renderer>(includeInactive: true);
        }
        catch (Exception)
        {
            // Keep an empty list: no renderer info means we always apply.
        }

        return state;
    }

    /// <summary>
    /// Prefix for TimbermeshAnimator.UpdateAnimationUpdaters. Returns true to
    /// run the vanilla pose update, false to skip it.
    /// </summary>
    public static bool ShouldApplyPose(Component animator)
    {
        if (!BenchmarkSettings.EnableInvisibleAnimatorPoseSkip ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        var state = States.GetValue(animator, CreateState);
        var renderers = state.Renderers;
        if (renderers.Length == 0)
        {
            return true;
        }

        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer != null && renderer.isVisible)
            {
                state.PoseDirty = false;
                return true;
            }
        }

        state.PoseDirty = true;
        return false;
    }

    /// <summary>
    /// Prefix for TimbermeshAnimator.UpdateAnimation: if a one-shot animation
    /// finished while off-screen (so the vanilla update no longer applies
    /// poses), apply the final pose once the animator is visible again. The
    /// fast path is one dictionary probe + a bool; reflection only runs while
    /// a pose is actually pending.
    /// </summary>
    public static void RepairFinishedPose(Component animator)
    {
        if (!BenchmarkSettings.EnableInvisibleAnimatorPoseSkip ||
            _updateAnimationUpdatersMethod is null ||
            _playingFinishedProperty is null)
        {
            return;
        }

        if (!States.TryGetValue(animator, out var state) || !state.PoseDirty)
        {
            return;
        }

        var renderers = state.Renderers;
        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer != null && renderer.isVisible)
            {
                // Only a FINISHED one-shot needs the repair here; a running
                // animation applies its pose in the vanilla update right after
                // this prefix anyway.
                try
                {
                    if (_playingFinishedProperty.GetValue(animator) is true)
                    {
                        state.PoseDirty = false;
                        _updateAnimationUpdatersMethod.Invoke(animator, null);
                    }
                }
                catch (Exception)
                {
                    // Vanilla pose stays as-is; purely cosmetic.
                }

                return;
            }
        }
    }

    public static void Initialize(MethodInfo updateAnimationUpdatersMethod, PropertyInfo playingFinishedProperty)
    {
        _updateAnimationUpdatersMethod = updateAnimationUpdatersMethod;
        _playingFinishedProperty = playingFinishedProperty;
    }
}
