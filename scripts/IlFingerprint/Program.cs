using System.Reflection;
using System.Security.Cryptography;

// Prints SHA256 of the raw IL bytes (as MethodBody.GetILAsByteArray returns
// them at runtime) for reviewed game methods, per game installation.
// Usage: IlFingerprint <Managed dir> [<Managed dir> ...]
var targets = new (string Assembly, string Type, string Method)[]
{
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntity", "Tick"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntity", "TickTickableComponents"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.MeteredTickableComponent", "StartAndTick"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.MeteredTickableComponent", "Tick"),
    ("Timberborn.WaterSystemRendering", "Timberborn.WaterSystemRendering.DataTextureArray`1", "UpdateTextureArrays"),
    ("Timberborn.SingletonSystem", "Timberborn.SingletonSystem.EventBus", "RegisterMethod"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntityBucket", "TickAll"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntityBucket", "Add"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntityBucket", "Remove"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntity", ".ctor"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.BaseComponent", "EnableComponent"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.BaseComponent", "DisableComponent"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.BaseComponent", "set_Enabled"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.ComponentCache", "OnDestroy"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.ComponentCache", "Initialize"),
    ("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.BaseComponent", "get_Enabled"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.MeteredTickableComponent", "get_Enabled"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "MoveAlongPath"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "ReachedLastPathCorner"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "GetSpeedLimitIfCloseToTarget"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "GetMovementSpeed"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "GetRemainingDistance"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "GetTimeFromLastPathPoint"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "MoveInDirection"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "AddAnimatedPathCorner"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "AddSmoothingAnimatedPathCorner"),
    ("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.PathFollower", "NotifyAfterMovement"),
    ("Timberborn.Navigation", "Timberborn.Navigation.NavigationService", "InStoppingProximity"),
    ("Timberborn.WalkingSystem", "Timberborn.WalkingSystem.WalkerSpeedManager", "GetWalkerSpeedAtCurrentPosition"),
    ("Timberborn.WalkingSystem", "Timberborn.WalkingSystem.WalkerSpeedManager", "GetWalkerBaseSpeed"),
    ("Timberborn.WalkingSystem", "Timberborn.WalkingSystem.WalkerMover", "Move"),
    ("Timberborn.TubeSystem", "Timberborn.TubeSystem.TubeVisitor", "UpdateVisit"),
    ("Timberborn.TubeSystem", "Timberborn.TubeSystem.TubeVisitor", "ExitTube"),
    ("Timberborn.TubeSystem", "Timberborn.TubeSystem.Tube", "RemoveVisitor"),
};
foreach (var managed in args)
{
    var paths = Directory.GetFiles(managed, "*.dll").ToList();
    using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
    Console.WriteLine("== " + managed);
    foreach (var (assemblyName, typeName, methodName) in targets)
    {
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(managed, assemblyName + ".dll"));
            var type = assembly.GetType(typeName, true)!;
            MethodBase? method = methodName == ".ctor"
                ? type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault()
                : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == methodName &&
                        ((typeName, methodName) switch
                        {
                            ("Timberborn.TubeSystem.TubeVisitor", "UpdateVisit") => m.GetParameters().Length == 0,
                            ("Timberborn.TubeSystem.TubeVisitor", "ExitTube") => m.GetParameters()
                                .Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "Timberborn.TubeSystem.Tube", "System.Boolean" }),
                            ("Timberborn.TubeSystem.Tube", "RemoveVisitor") => m.GetParameters()
                                .Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "Timberborn.TubeSystem.TubeVisitor" }),
                            _ => true
                        }));
            if (method == null) { Console.WriteLine($"{typeName}.{methodName}: (absent)"); continue; }
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var clauses = method.GetMethodBody()!.ExceptionHandlingClauses.Count;
            Console.WriteLine($"{typeName}.{methodName}: {Convert.ToHexString(SHA256.HashData(il))} bytes={il.Length} eh={clauses} mvid={assembly.ManifestModule.ModuleVersionId}");
        }
        catch (Exception e) { Console.WriteLine($"{typeName}.{methodName}: ERROR {e.GetType().Name}: {e.Message}"); }
    }
}
