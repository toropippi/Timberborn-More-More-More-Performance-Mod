using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class MechanicalGraphLoadBatcher
{
    private const string PostInitializeStageName = "Timberborn.WorldPersistence.EntitiesLoader.PostInitialize";

    private static readonly object LockObject = new object();
    private static readonly Dictionary<object, BatchContext> Contexts = new Dictionary<object, BatchContext>(ReferenceComparer.Instance);
    private static int _postInitializeDepth;
    private static int _flushing;
    private static long _sessionId;

    public static void BeginStage(string stageName)
    {
        if (!BenchmarkSettings.EnableMechanicalGraphLoadBatching || stageName != PostInitializeStageName)
        {
            return;
        }

        lock (LockObject)
        {
            if (_postInitializeDepth == 0)
            {
                Contexts.Clear();
            }

            _postInitializeDepth++;
        }
    }

    public static void EndStage(string stageName)
    {
        if (!BenchmarkSettings.EnableMechanicalGraphLoadBatching || stageName != PostInitializeStageName)
        {
            return;
        }

        BatchContext[] contexts;
        lock (LockObject)
        {
            if (_postInitializeDepth <= 0)
            {
                return;
            }

            _postInitializeDepth--;
            if (_postInitializeDepth > 0)
            {
                return;
            }

            contexts = Contexts.Values.ToArray();
            Contexts.Clear();
        }

        Flush(contexts);
    }

    public static bool TryDeferJoin(object factory, object? graphsArgument)
    {
        if (!BenchmarkSettings.EnableMechanicalGraphLoadBatching ||
            Volatile.Read(ref _postInitializeDepth) <= 0 ||
            Volatile.Read(ref _flushing) != 0 ||
            graphsArgument is not IEnumerable enumerable)
        {
            return false;
        }

        var graphs = new List<object>();
        foreach (var graph in enumerable)
        {
            if (graph is not null && !graphs.Any(existing => ReferenceEquals(existing, graph)))
            {
                graphs.Add(graph);
            }
        }

        if (graphs.Count <= 1)
        {
            return false;
        }

        lock (LockObject)
        {
            if (!Contexts.TryGetValue(factory, out var context))
            {
                context = new BatchContext(factory);
                Contexts.Add(factory, context);
            }

            context.DeferJoin(graphs);
        }

        return true;
    }

    private static void Flush(IReadOnlyCollection<BatchContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        var sessionId = Interlocked.Increment(ref _sessionId);
        var stopwatch = Stopwatch.StartNew();
        var deferredCalls = 0;
        var inputGraphReferences = 0;
        var uniqueGraphs = 0;
        var components = 0;
        var flushedComponents = 0;
        var flushedGraphs = 0;
        var errors = 0;
        Interlocked.Exchange(ref _flushing, 1);
        try
        {
            foreach (var context in contexts)
            {
                deferredCalls += context.DeferredJoinCalls;
                inputGraphReferences += context.InputGraphReferences;
                uniqueGraphs += context.UniqueGraphCount;

                var joinMethod = FindJoinMethod(context.Factory);
                if (joinMethod is null)
                {
                    errors++;
                    continue;
                }

                var graphType = joinMethod.GetParameters()[0].ParameterType.GetGenericArguments()[0];
                foreach (var component in context.Components())
                {
                    components++;
                    if (component.Count <= 1)
                    {
                        continue;
                    }

                    var array = Array.CreateInstance(graphType, component.Count);
                    for (var i = 0; i < component.Count; i++)
                    {
                        array.SetValue(component[i], i);
                    }

                    try
                    {
                        joinMethod.Invoke(context.Factory, new object[] { array });
                        flushedComponents++;
                        flushedGraphs += component.Count;
                    }
                    catch (Exception exception)
                    {
                        errors++;
                        Debug.LogWarning($"[T3MP] MechanicalGraphLoadBatch join failed: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }

        stopwatch.Stop();
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] MechanicalGraphLoadBatch session={0}, deferredCalls={1}, inputGraphs={2}, uniqueGraphs={3}, components={4}, flushedComponents={5}, flushedGraphs={6}, errors={7}, ms={8:F2}",
            sessionId,
            deferredCalls,
            inputGraphReferences,
            uniqueGraphs,
            components,
            flushedComponents,
            flushedGraphs,
            errors,
            stopwatch.Elapsed.TotalMilliseconds));
    }

    private static MethodInfo? FindJoinMethod(object factory)
    {
        return factory.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Join" || method.GetParameters().Length != 1)
                {
                    return false;
                }

                var parameterType = method.GetParameters()[0].ParameterType;
                return parameterType.IsGenericType &&
                    parameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            });
    }

    private sealed class BatchContext
    {
        private readonly Dictionary<object, object> _parent = new Dictionary<object, object>(ReferenceComparer.Instance);
        private readonly List<object> _graphs = new List<object>();

        public BatchContext(object factory)
        {
            Factory = factory;
        }

        public object Factory { get; }
        public int DeferredJoinCalls { get; private set; }
        public int InputGraphReferences { get; private set; }
        public int UniqueGraphCount => _graphs.Count;

        public void DeferJoin(IReadOnlyList<object> graphs)
        {
            DeferredJoinCalls++;
            InputGraphReferences += graphs.Count;
            for (var i = 0; i < graphs.Count; i++)
            {
                AddGraph(graphs[i]);
            }

            var first = graphs[0];
            for (var i = 1; i < graphs.Count; i++)
            {
                Union(first, graphs[i]);
            }
        }

        public IEnumerable<List<object>> Components()
        {
            var groups = new Dictionary<object, List<object>>(ReferenceComparer.Instance);
            foreach (var graph in _graphs)
            {
                var root = Find(graph);
                if (!groups.TryGetValue(root, out var group))
                {
                    group = new List<object>();
                    groups.Add(root, group);
                }

                group.Add(graph);
            }

            return groups.Values;
        }

        private void AddGraph(object graph)
        {
            if (_parent.ContainsKey(graph))
            {
                return;
            }

            _parent.Add(graph, graph);
            _graphs.Add(graph);
        }

        private void Union(object left, object right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (!ReferenceEquals(leftRoot, rightRoot))
            {
                _parent[rightRoot] = leftRoot;
            }
        }

        private object Find(object graph)
        {
            var parent = _parent[graph];
            if (ReferenceEquals(parent, graph))
            {
                return graph;
            }

            var root = Find(parent);
            _parent[graph] = root;
            return root;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
