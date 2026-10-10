// Prints the LoadBodies fingerprints of one or more game installs with the
// same code the product runs (each install in its own load context).
// Usage from the repo root:
//   dotnet run -c Release --project scripts/LoadPrints -- [-v] <Timberborn_Data/Managed> [...]
// A print missing from LoadBodies.Accepted means a relied-on game body changed:
// review it (-v lists every method print), then add the new print.
using System.Reflection;
using System.Runtime.Loader;
using T3MP.Loading;
var verbose = args.Contains("-v");
foreach (var managed in args.Where(a => a != "-v")) {
  var ctx = new GameContext(managed);
  var asms = Directory.GetFiles(managed, "Timberborn.*.dll").Concat(Directory.GetFiles(managed, "Bindito*.dll"))
    .Select(f => { try { return ctx.LoadFromAssemblyPath(f); } catch { return null; } }).Where(a => a != null).ToList();
  var cache = new Dictionary<string, Type?>();
  Type? Resolve(string name) {
    if (cache.TryGetValue(name, out var t)) return t;
    foreach (var a in asms) { try { t = a!.GetType(name, false); } catch { t = null; } if (t != null) break; }
    return cache[name] = t;
  }
  Console.WriteLine("== " + managed);
  foreach (var set in LoadBodies.All) {
    var missing = new List<string>(); var lines = new List<string>();
    string hash;
    try { hash = LoadBodies.Fingerprint(set, Resolve, missing, lines); }
    catch (Exception e) { Console.WriteLine($"  {set.Name}: ERROR {e.GetType().Name}: {e.Message}"); continue; }
    Console.WriteLine($"  {set.Name}: {hash}" + (missing.Count > 0 ? "  MISSING " + string.Join(", ", missing) : ""));
    if (verbose) foreach (var l in lines) Console.WriteLine("      " + l);
    foreach (var m in missing) {
      var simple = m.Substring(0, m.IndexOf("::")).Split('|')[0]; simple = simple.Substring(simple.LastIndexOfAny(new[]{'.','+'}) + 1);
      var hits = asms.SelectMany(a => { try { return a!.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null)!; } }).Where(x => x!.Name == simple).Select(x => x!.FullName).Distinct().Take(3);
      Console.WriteLine($"      ? {m} -> {string.Join(" ; ", hits)}");
    }
  }
}
sealed class GameContext : AssemblyLoadContext {
  private readonly string _dir;
  private static readonly HashSet<string> Framework = new(StringComparer.OrdinalIgnoreCase) { "mscorlib", "System", "System.Core", "netstandard" };
  public GameContext(string dir) : base(dir, false) { _dir = dir; }
  protected override Assembly? Load(AssemblyName name) {
    if (Framework.Contains(name.Name!) || name.Name!.StartsWith("System.Runtime") || name.Name == "System.Private.CoreLib") return null;
    var path = Path.Combine(_dir, name.Name + ".dll");
    return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
  }
}
