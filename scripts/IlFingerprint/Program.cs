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
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == methodName);
            if (method == null) { Console.WriteLine($"{typeName}.{methodName}: (absent)"); continue; }
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var clauses = method.GetMethodBody()!.ExceptionHandlingClauses.Count;
            Console.WriteLine($"{typeName}.{methodName}: {Convert.ToHexString(SHA256.HashData(il))} bytes={il.Length} eh={clauses} mvid={assembly.ManifestModule.ModuleVersionId}");
        }
        catch (Exception e) { Console.WriteLine($"{typeName}.{methodName}: ERROR {e.GetType().Name}: {e.Message}"); }
    }
}
