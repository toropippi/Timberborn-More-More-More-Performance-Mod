using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.EntitySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Test-only observer: samples every character's simulation position against its
// visual model position and reports models that stay frozen while the entity
// keeps moving (the "stuck in a tube" symptom). Never repairs anything.
// Enabled by -t3mpTestModelGap.
public sealed class ModelGapMonitor : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const float IntervalSeconds = 5f;
    private EntityRegistry _registry = null!;
    private MethodInfo _getMovementAnimator = null!;
    private FieldInfo _modelField = null!;
    private PropertyInfo _modelPosition = null!;
    private readonly Dictionary<Guid, (Vector3 Entity, Vector3 Model, int Frozen)> _previous = new Dictionary<Guid, (Vector3, Vector3, int)>();
    private float _next;
    private int _sample;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestModelGap", StringComparison.OrdinalIgnoreCase));

    internal void Initialize(EntityRegistry registry)
    {
        _registry = registry;
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var movement = Find("Timberborn.CharacterMovementSystem.MovementAnimator");
        _getMovementAnimator = typeof(Timberborn.BaseComponentSystem.BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(movement);
        _modelField = movement.GetField("_characterModel", All) ?? throw new MissingFieldException("MovementAnimator._characterModel");
        _modelPosition = _modelField.FieldType.GetProperty("Position", All) ?? throw new MissingMemberException("CharacterModel.Position");
        _next = Time.realtimeSinceStartup + IntervalSeconds;
        Debug.Log("[T3MPTEST] Model gap monitor started.");
    }

    private void LateUpdate()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + IntervalSeconds;
        _sample++;
        int characters = 0, moving = 0, largeGap = 0, frozen = 0, frozenTwice = 0;
        float maxGap = 0f;
        var examples = new List<string>();
        var seen = new HashSet<Guid>();
        foreach (var entity in _registry.Entities)
        {
            if (entity == null) continue;
            var animator = _getMovementAnimator.Invoke(entity, null);
            if (animator == null) continue;
            var model = _modelField.GetValue(animator);
            if (model == null) continue;
            characters++;
            var entityPosition = entity.Transform.position;
            var modelPosition = (Vector3)_modelPosition.GetValue(model)!;
            var gap = Vector3.Distance(entityPosition, modelPosition);
            if (gap > maxGap) maxGap = gap;
            if (gap > 2f) largeGap++;
            var key = entity.EntityId;
            seen.Add(key);
            var count = 0;
            if (_previous.TryGetValue(key, out var previous))
            {
                var entityMoved = Vector3.Distance(previous.Entity, entityPosition) > 0.5f;
                var modelStill = Vector3.Distance(previous.Model, modelPosition) < 0.001f;
                if (entityMoved) moving++;
                if (entityMoved && modelStill)
                {
                    count = previous.Frozen + 1;
                    frozen++;
                    if (count >= 2)
                    {
                        frozenTwice++;
                        if (examples.Count < 3) examples.Add($"{key} gap={gap:F2} entity={entityPosition} model={modelPosition} streak={count}");
                    }
                }
            }
            _previous[key] = (entityPosition, modelPosition, count);
        }
        foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(stale);
        Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[T3MPTEST] modelgap sample={0} timeScale={1:F1} characters={2} moving={3} gapOver2={4} maxGap={5:F2} frozenModelWhileMoving={6} frozenTwice={7}{8}",
            _sample, Time.timeScale, characters, moving, largeGap, maxGap, frozen, frozenTwice, examples.Count > 0 ? " examples: " + string.Join(" | ", examples) : ""));
    }
}
