using System.Reflection;
using System.Security.Cryptography;

// Prints SHA256 of the raw IL bytes (as MethodBody.GetILAsByteArray returns
// them at runtime) for reviewed game methods, per game installation.
// Usage: IlFingerprint <Managed dir> [<Managed dir> ...]
var targets = new (string Assembly, string Type, string Method)[]
{
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder/4"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindClosestYielders"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "AddCloserYielder"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ReachableYielder", "CompareTo"),
    ("Timberborn.Yielding", "Timberborn.Yielding.Yielder", "get_InstantiationOrder"),
    ("Timberborn.Navigation", "Timberborn.Navigation.TerrainReachabilityService", "VisitNeighbors"),
    ("Timberborn.Navigation", "Timberborn.Navigation.TerrainReachabilityService", "VisitNode"),
    ("Timberborn.Navigation", "Timberborn.Navigation.TerrainReachabilityService+NodeToVisit", "get_Distance"),
    ("Timberborn.Navigation", "Timberborn.Navigation.NavMeshNode", "get_Cost"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntity", "Tick"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntity", "TickTickableComponents"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.MeteredTickableComponent", "StartAndTick"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.MeteredTickableComponent", "Tick"),
    ("Timberborn.TickSystem", "Timberborn.TickSystem.TickableComponent", "StartAndTick"),
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
    ("Timberborn.TimeSystem", "Timberborn.TimeSystem.NonlinearAnimationManager", "get_SpeedMultiplier"),
    ("Timberborn.TimeSystem", "Timberborn.TimeSystem.NonlinearAnimationManager", "get_NonlinearSpeed"),
    ("Timberborn.InventorySystem", "Timberborn.InventorySystem.Inventory", "GetCapacity"),
    ("Timberborn.InventorySystem", "Timberborn.InventorySystem.Inventory", "LimitedAmount"),
    ("Timberborn.InventorySystem", "Timberborn.InventorySystem.InventoryFillCalculator", "GetInventoryFillPercentage"),
    ("Timberborn.Goods", "Timberborn.Goods.StorableGoodRegistry", "GetAmount"),
    ("Timberborn.Goods", "Timberborn.Goods.StorableGoodRegistry", "Add/1"),
    ("Timberborn.InventorySystem", "Timberborn.InventorySystem.Inventory", "get_AllowedGoods"),
    ("Timberborn.Goods", "Timberborn.Goods.StorableGoodRegistry", "get_Goods"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "UpdateNeed"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "CheckNewState"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "GetNeed"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "NeedIsCritical"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "NeedIsInCriticalState"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "NeedIsBelowWarningThreshold"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager", "TryAppraise/3"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsAtMinimumPoints"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsFavorable"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsInCriticalState"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsActive"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsBelowWarningThreshold"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_Points"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_Enabled"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_IsCritical"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "Update"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "TryAppraise/2"),
    ("Timberborn.NeedBehaviorSystem", "Timberborn.NeedBehaviorSystem.NeedFilter", "Filter"),
    ("Timberborn.NeedBehaviorSystem", "Timberborn.NeedBehaviorSystem.Appraiser", "AppraiseEffects/2"),
    ("Timberborn.ModularShafts", "Timberborn.ModularShafts.ModularShaftAnimator", "UpdateAnimation"),
    ("Timberborn.MechanicalSystem", "Timberborn.MechanicalSystem.MechanicalNode", "get_PowerEfficiency"),
    ("Timberborn.MechanicalSystem", "Timberborn.MechanicalSystem.MechanicalNode", "get_ActiveAndPowered"),
    ("Timberborn.MechanicalSystem", "Timberborn.MechanicalSystem.MechanicalGraph", "get_PowerEfficiency"),
    ("Timberborn.TimbermeshAnimations", "Timberborn.TimbermeshAnimations.TimbermeshAnimator", "set_Enabled"),
    ("Timberborn.TimbermeshAnimations", "Timberborn.TimbermeshAnimations.TimbermeshAnimator", "set_Speed"),
    ("Timberborn.Navigation", "Timberborn.Navigation.FlowFieldPathBuilder", "AddEdgeNode"),
    ("Timberborn.Navigation", "Timberborn.Navigation.TerrainNavMeshGraph", "GetConnectionCost"),
    ("Timberborn.Navigation", "Timberborn.Navigation.TerrainNavMeshGraph", "GetGroupId"),
    ("Timberborn.Navigation", "Timberborn.Navigation.NodeIdService", "Distance/2"),
    ("Timberborn.Navigation", "Timberborn.Navigation.NodeIdService", "IdToWorld"),
    ("Timberborn.NeedSystem", "Timberborn.NeedSystem.Need", "get_NeedSpec"),
    ("Timberborn.NeedSpecs", "Timberborn.NeedSpecs.NeedSpec", "get_MinimumValue"),
    ("Timberborn.ModularShafts", "Timberborn.ModularShafts.ModularShaftAnimator", "get_IsAnimated"),
    ("Timberborn.ModularShafts", "Timberborn.ModularShafts.ModularShaftAnimator", "set_IsAnimated"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.YielderFinder", "FindLivingYielderWithoutAccessible"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.YielderFinder", "FindYielderWithAccessible"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.YielderFinder", "RegularYielderAsReachable"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.YielderFinder", "AccessibleYielderAsReachable"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindLivingYielder"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder/2"),
    ("Timberborn.Yielding", "Timberborn.Yielding.Yielder", "get_IsYielding"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.YielderExtensions", "IsAlive"),
    ("Timberborn.YielderFinding", "Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder/3"),
    ("Timberborn.Yielding", "Timberborn.Yielding.Yielder", "get_Yield"),
    ("Timberborn.NaturalResourcesLifecycle", "Timberborn.NaturalResourcesLifecycle.LivingNaturalResource", "get_IsDead"),
    ("Timberborn.Yielding", "Timberborn.Yielding.Yielder", "get_CenterPosition"),
    ("Timberborn.Navigation", "Timberborn.Navigation.Accessible", "FindTerrainPath/V"),
    ("Timberborn.Navigation", "Timberborn.Navigation.Accessible", "FindTerrainPath/A"),
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
                    .FirstOrDefault(m => m.Name == methodName.Split('/')[0] &&
                        ((typeName, methodName) switch
                        {
                            ("Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder") => m.GetParameters().Length == 2,
                            ("Timberborn.YielderFinding.ClosestYielderFinder", "FindYielder/4") => m.GetParameters().Length == 4,
                            ("Timberborn.Goods.StorableGoodRegistry", "Add/1") => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.FullName == "Timberborn.Goods.StorableGoodAmount",
                            ("Timberborn.Navigation.NodeIdService", "Distance/2") => m.GetParameters().Length == 2 && m.GetParameters().All(p => p.ParameterType.FullName == "System.Int32"),
                            ("Timberborn.Navigation.Accessible", "FindTerrainPath/V") => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.FullName == "UnityEngine.Vector3",
                            ("Timberborn.Navigation.Accessible", "FindTerrainPath/A") => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.FullName == "Timberborn.Navigation.Accessible",
                            _ when methodName.Contains('/') => m.GetParameters().Length == int.Parse(methodName.Split('/')[1]),
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
