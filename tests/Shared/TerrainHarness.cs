using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Shared native-game harness for the isolated prototype and production tests.
readonly record struct Edge(int Id, float Cost);

sealed class Harness
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    readonly AssemblyLoadContext context;
    readonly Type graphType, nodeType, restrictedType, serviceType;
    internal Assembly Navigation { get; private set; } = null!;
    internal Assembly LoadCode(string path) => context.LoadFromAssemblyPath(Path.GetFullPath(path));
    readonly object graph, restricted, service;
    readonly Action<int, int, List<int>> search;
    readonly List<int> results = new();

    internal Harness(string managed, string path)
    {
        context = new AssemblyLoadContext(Guid.NewGuid().ToString(), isCollectible: true);
        context.Resolving += (_, name) => {
            if (name.Name is "netstandard" or "mscorlib" || name.Name!.StartsWith("System")) return null;
            return context.LoadFromAssemblyPath(Path.Combine(managed, name.Name + ".dll"));
        };
        var assembly = context.LoadFromAssemblyPath(path);
        Navigation = assembly;
        Type Find(string name) => assembly.GetType("Timberborn.Navigation." + name, true)!;
        graphType = Find("TerrainNavMeshGraph"); nodeType = Find("NavMeshNode");
        restrictedType = Find("RestrictedNodeMap"); serviceType = Find("TerrainReachabilityService");
        graph = Activator.CreateInstance(graphType, new object?[] { null, null })!;
        restricted = Activator.CreateInstance(restrictedType, new object?[] { null })!;
        service = Activator.CreateInstance(serviceType, new[] { graph, restricted })!;
        search = serviceType.GetMethod("GetReachableNeighborsInRange", Flags)!.CreateDelegate<Action<int, int, List<int>>>(service);
    }

    internal void SetGraph(Edge[][] edges, bool[] restrictions)
    {
        var listType = typeof(List<>).MakeGenericType(nodeType);
        var array = Array.CreateInstance(listType, edges.Length);
        for (var i = 0; i < edges.Length; i++)
        {
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var edge in edges[i]) list.Add(Activator.CreateInstance(nodeType, edge.Id, 0, edge.Cost));
            array.SetValue(list, i);
        }
        graphType.GetField("_allNeighbors", Flags)!.SetValue(graph, array);
        restrictedType.GetField("_nodes", Flags)!.SetValue(restricted, restrictions.ToArray());
    }

    internal void SetComparer(IEqualityComparer<int> comparer) => serviceType.GetField("_visitedNodes", Flags)!
        .SetValue(service, new HashSet<int>(comparer));

    internal string Run(int start, int range)
    {
        results.Clear(); results.Add(123456); // caller-owned list must append.
        string? error = null;
        try { search(start, range, results); } catch (Exception e) { error = e.GetType().FullName; }
        var visited = (HashSet<int>)serviceType.GetField("_visitedNodes", Flags)!.GetValue(service)!;
        var queue = (IEnumerable)serviceType.GetField("_nodesToVisit", Flags)!.GetValue(service)!;
        var queued = queue.Cast<object>().Select(item => new {
            id = item.GetType().GetProperty("Id")!.GetValue(item),
            bits = BitConverter.SingleToInt32Bits((float)item.GetType().GetProperty("Distance")!.GetValue(item)!)
        }).ToArray();
        return JsonSerializer.Serialize(new { results, visited = visited.ToArray(), queued, error });
    }

    internal double Time(int repeats)
    {
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++) { results.Clear(); search(64 * 32 + 32, 5, results); }
        return timer.Elapsed.TotalMilliseconds;
    }
}
