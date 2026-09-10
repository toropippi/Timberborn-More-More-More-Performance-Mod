using System;
using System.Collections.Generic;
using System.Reflection;
using Timberborn.TickSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// TickableEntity.Tick with the vanilla enumerator replaced by index access and
// one exact shortcut: an alive entity whose tickable components are all
// disabled returns before reading activeInHierarchy, because vanilla would only
// read that flag and then run an empty loop. Every Enabled flag is read live at
// the moment of the visit; nothing is cached, reordered or skipped otherwise.
// The exception wrapper reproduces the vanilla message verbatim.
internal static class TickEntityFast
{
    private const string Owner = "t3mp.runtime.tick";
    private static Action<MeteredTickableComponent>? _tick;
    private static RuntimePatches.Shape? _shape;
    private static Type? _harmonyType;
    private static MethodInfo? _loop;
    private static bool _passThroughReported;
    internal static bool Installed { get; private set; }

    // Raw-IL SHA256 of the reviewed vanilla bodies: 1.0.13.1 and 1.1.2.0/1.1.2.4.
    internal static readonly string[] ReviewedTick =
    {
        "1505E4964C1B5B1243D4E48190FCC49617B6A34362A012A60937B1AF26130C34",
        "9DC6FCDBD41B512C73522FBD1AE22893ADA21277BB8E56621F44D34DB159E2CA"
    };
    internal static readonly string[] ReviewedLoop =
    {
        "F7D063689233726D8BB0CE335466978A40CC9B86E088D99721D5A18216119180",
        "ADF101E3EEB721D1535EC28BAC0105EB26853DB0617C8998EA65F4808BC6F5CB"
    };

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        var entity = typeof(TickableEntity);
        var tick = entity.GetMethod("Tick", RuntimePatches.All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        var loop = entity.GetMethod("TickTickableComponents", RuntimePatches.All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        // 1.0 calls MeteredTickableComponent.StartAndTick; 1.1 calls Tick. Bind
        // whichever the running game defines so one DLL serves both.
        var metered = typeof(MeteredTickableComponent);
        var call = metered.GetMethod("StartAndTick", RuntimePatches.All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                   ?? metered.GetMethod("Tick", RuntimePatches.All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        if (tick == null || loop == null || call == null || call.ReturnType != typeof(void))
        {
            Debug.LogWarning("[T3MP] Tick traversal: unexpected TickSystem shape; vanilla retained.");
            return;
        }
        // Raw IL plus the parts the IL bytes do not cover: exactly one catch-all
        // clause for System.Exception, as the reviewed builds have.
        var clauses = tick.GetMethodBody()?.ExceptionHandlingClauses;
        if (!RuntimePatches.ReviewedBody(tick, ReviewedTick) || !RuntimePatches.ReviewedBody(loop, ReviewedLoop) ||
            clauses == null || clauses.Count != 1 || clauses[0].Flags != ExceptionHandlingClauseOptions.Clause || clauses[0].CatchType != typeof(Exception) ||
            loop.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
        {
            Debug.LogWarning("[T3MP] Tick traversal: TickableEntity body is not a reviewed build; vanilla retained.");
            return;
        }
        _harmonyType = harmonyType;
        _loop = loop;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            // A body replacement would hide another mod's transpiler on Tick and
            // bypass any patch on the helper the replacement no longer calls.
            if (RuntimePatches.ForeignPatched(harmonyType, tick, Owner, transpilersOnly: true) ||
                RuntimePatches.ForeignPatched(harmonyType, loop, Owner, transpilersOnly: false))
                throw new InvalidOperationException("another mod patches TickableEntity");
            _shape = RuntimePatches.OriginalShape(harmonyType, tick);
            _tick = (Action<MeteredTickableComponent>)Delegate.CreateDelegate(typeof(Action<MeteredTickableComponent>), call);
            apply(tick, null, null, nameof(Rewrite), null);
        }, typeof(TickEntityFast));
        if (Installed) Debug.Log("[T3MP] Tick traversal installed (component call=" + call.Name + ").");
    }

    // Called at every world load: a mod that patched the bypassed helper after
    // this install would otherwise be silently ignored. Restores vanilla.
    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _loop == null) return;
        try
        {
            var tick = typeof(TickableEntity).GetMethod("Tick", RuntimePatches.All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)!;
            if (!RuntimePatches.ForeignPatched(_harmonyType, _loop, Owner, transpilersOnly: false) &&
                !RuntimePatches.ForeignPatched(_harmonyType, tick, Owner, transpilersOnly: true)) return;
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
            Debug.Log("[T3MP] Tick traversal removed: another mod now patches TickableEntity; vanilla restored.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Tick traversal revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // Replaces the body only when the stream Harmony hands over is the vanilla
    // one. Another mod's transpiler that ran earlier (or is added later, when
    // Harmony re-runs the chain) is passed through untouched.
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (_shape == null || !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            if (!_passThroughReported)
            {
                _passThroughReported = true;
                Debug.Log("[T3MP] Tick traversal: TickableEntity.Tick was changed by another mod; passing its instructions through unchanged.");
            }
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(TickEntityFast).GetMethod(nameof(TickFast), RuntimePatches.All)!, 1);
    }

    internal static void TickFast(TickableEntity entity)
    {
        try
        {
            var gameObject = entity._entityComponent.GameObject;
            var components = entity._tickableComponents;
            // Alive object, nothing enabled: vanilla reads activeInHierarchy and
            // runs an empty loop, so returning here changes nothing. The scan
            // reads the same field-backed Enabled flags vanilla reads. Should a
            // malformed component make a read throw, the vanilla order below
            // raises (or skips) it exactly as vanilla would.
            bool needsVisit;
            try
            {
                needsVisit = false;
                var length = components.Length;
                for (var i = 0; i < length; i++)
                {
                    var component = components[i];
                    if (component == null || component.Enabled)
                    {
                        needsVisit = true;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                needsVisit = true;
            }
            if (!needsVisit && gameObject) return;
            if (gameObject!.activeInHierarchy)
            {
                var tick = _tick!;
                var count = components.Length;
                for (var i = 0; i < count; i++)
                {
                    var component = components[i];
                    if (component.Enabled) tick(component);
                }
            }
        }
        catch (Exception innerException)
        {
            var text = $"Exception thrown while ticking entity {entity.EntityId}";
            text = !entity._entityComponent
                ? text + " '" + entity._originalName + "' (destroyed)"
                : text + " '" + entity._entityComponent.Name + "'";
            throw new Exception(text, innerException);
        }
    }
}
