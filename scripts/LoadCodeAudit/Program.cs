using Mono.Cecil;

if (args.Length is < 1 or > 2) throw new ArgumentException("Usage: LoadCodeAudit game-managed-directory [model-access|ranged-events|nav-removal|load-ui|random|tube-lighting|adapter-usage|adapter-roles|preinitializers|construction-readers|collider-creators]");
if (args.Length == 2 && args[1] == "adapter-roles")
{
    // Retain names only after disposing each module. Interface ancestry is
    // sufficient for these two non-generic contracts, including generic bases.
    var shapes = new Dictionary<string, (string? Base, string[] Interfaces)>();
    foreach (var path in Directory.GetFiles(args[0], "Timberborn.*.dll"))
    {
        using var module = ModuleDefinition.ReadModule(path);
        var pending = new Stack<TypeDefinition>(module.Types);
        while (pending.TryPop(out var type))
        {
            foreach (var child in type.NestedTypes) pending.Push(child);
            shapes[type.FullName] = (type.BaseType?.GetElementType().FullName,
                type.Interfaces.Select(i => i.InterfaceType.GetElementType().FullName).ToArray());
        }
    }
    var contracts = new Dictionary<string, int> {
        ["Timberborn.BaseComponentSystem.IUpdatableComponent"] = 1,
        ["Timberborn.BaseComponentSystem.ILateUpdatableComponent"] = 2 };
    var masks = new Dictionary<string, int>();
    int Mask(string? name)
    {
        if (name == null) return 0;
        if (contracts.TryGetValue(name, out var contract)) return contract;
        if (masks.TryGetValue(name, out var cached)) return cached;
        if (!shapes.TryGetValue(name, out var shape)) return 0;
        var mask = Mask(shape.Base);
        foreach (var i in shape.Interfaces) mask |= Mask(i);
        return masks[name] = mask;
    }
    foreach (var name in shapes.Keys.OrderBy(x => x)) Console.WriteLine(name + "\t" + Mask(name));
    return;
}
var adapterUsage = args.Length == 2 && args[1] == "adapter-usage";
var modelAccess = args.Length == 2 && args[1] == "model-access";
var rangedEvents = args.Length == 2 && args[1] == "ranged-events";
var navRemoval = args.Length == 2 && args[1] == "nav-removal";
var loadUi = args.Length == 2 && args[1] == "load-ui";
var randomCalls = args.Length == 2 && args[1] == "random";
var tubeLighting = args.Length == 2 && args[1] == "tube-lighting";
var preinitializers = args.Length == 2 && args[1] == "preinitializers";
var constructionReaders = args.Length == 2 && args[1] == "construction-readers";
var colliderCreators = args.Length == 2 && args[1] == "collider-creators";
foreach (var path in Directory.GetFiles(args[0], "Timberborn.*.dll"))
{
    using var module = ModuleDefinition.ReadModule(path);
    var pending = new Stack<TypeDefinition>(module.Types);
    while (pending.TryPop(out var type))
    {
        foreach (var nested in type.NestedTypes) pending.Push(nested);
        if (preinitializers)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody && m.Name.EndsWith("PreInitializeEntity", StringComparison.Ordinal)))
                Console.WriteLine(module.Name + " " + method.FullName);
            continue;
        }
        if (loadUi && type.Interfaces.Any(i => i.InterfaceType.FullName == "Timberborn.BottomBarSystem.IBottomBarElementsProvider"))
            Console.WriteLine("PROVIDER " + module.Name + " " + type.FullName);
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (colliderCreators)
            {
                if (instruction.Operand is GenericInstanceMethod call && call.Name == "AddComponent" &&
                    call.GenericArguments.Any(t => t.FullName.EndsWith("Collider", StringComparison.Ordinal)))
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (constructionReaders)
            {
                if (instruction.Operand is MemberReference member &&
                    (member.DeclaringType?.FullName == "Timberborn.Buildings.BuildingModel" && member.Name.Contains("Unfinished") ||
                     member.DeclaringType?.FullName == "Timberborn.ConstructionSites.ConstructionSiteProgressVisualizer"))
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (adapterUsage)
            {
                static bool Adapter(string name) => name is "Timberborn.BaseComponentSystem.BaseComponentUpdateUnityAdapter" or
                    "Timberborn.BaseComponentSystem.BaseComponentLateUpdateUnityAdapter" or "Timberborn.BaseComponentSystem.BaseComponentUnityAdapter";
                if (instruction.Operand is MemberReference member && member.DeclaringType != null && Adapter(member.DeclaringType.FullName) ||
                    instruction.Operand is GenericInstanceMethod generic && generic.GenericArguments.Any(t => Adapter(t.FullName)))
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (tubeLighting)
            {
                static bool LightingType(string name) => name is "Timberborn.Rendering.MaterialLightingRenderers" or "Timberborn.Rendering.MaterialLightingEnabler" or "Timberborn.TubeSystem.TubeModel";
                if (instruction.Operand is MemberReference lighting && lighting.DeclaringType != null && LightingType(lighting.DeclaringType.FullName) ||
                    instruction.Operand is GenericInstanceMethod generic && generic.GenericArguments.Any(t => LightingType(t.FullName)))
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (randomCalls)
            {
                if (instruction.Operand is MethodReference random && random.DeclaringType.FullName is "UnityEngine.Random" or "Timberborn.Common.RandomNumberGenerator" or "Timberborn.Common.IRandomNumberGenerator")
                    Console.WriteLine(method.FullName + " -> " + random.FullName);
                continue;
            }
            if (loadUi)
            {
                if (instruction.Operand is MemberReference member &&
                    member.DeclaringType?.FullName is "Timberborn.PlantingUI.PlantablePreviewService" or "Timberborn.PlantingUI.PlantablePreview")
                    Console.WriteLine(method.FullName + " -> " + member.FullName);
                continue;
            }
            if (navRemoval)
            {
                if (instruction.Operand is FieldReference navField && navField.DeclaringType.FullName == "Timberborn.Navigation.NavMeshObject" &&
                    navField.Name is "_addingChanges" or "_removingChanges")
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (rangedEvents)
            {
                if (instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Timberborn.RangedEffectSystem.RangedEffectApplier" && field.Name == "ActiveChanged" ||
                    instruction.Operand is MethodReference call && call.DeclaringType.FullName == "Timberborn.RangedEffectSystem.RangedEffectApplier" && call.Name.Contains("ActiveChanged"))
                    Console.WriteLine(method.FullName + " -> " + instruction.Operand);
                continue;
            }
            if (instruction.Operand is MethodReference target &&
                (modelAccess ?
                 target.DeclaringType.FullName is "Timberborn.GoodStackSystem.GoodStackModel" or "Timberborn.Rendering.EntityMaterials" ||
                 target.Name.StartsWith("GetComponentsInChildren") && target.FullName.Contains("UnityEngine.") :
                 target.DeclaringType.FullName == "System.GC" && target.Name == "Collect" ||
                 target.Name is "set_GCMode" or "UnloadUnusedAssets" or "CollectIncremental" ||
                 target.DeclaringType.FullName == "Timberborn.MechanicalSystem.TransputMap" && target.Name.StartsWith("add_")))
                Console.WriteLine(method.FullName + " -> " + target.FullName);
        }
    }
}
