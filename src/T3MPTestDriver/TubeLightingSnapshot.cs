using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Timberborn.EntitySystem;
using UnityEngine;

namespace T3MPTestDriver;

internal static class TubeLightingSnapshot
{
    internal static void Report(IEnumerable<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestTubeLightingSnapshot")) return;
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        using var output = new StreamWriter(Path.Combine(directory, "tube-lighting.tsv"));
        var count = 0; var rendererCount = 0;
        foreach (var entity in entities.OrderBy(e => e.EntityId))
        {
            if (!entity.AllComponents.Any(c => c.GetType().FullName == "Timberborn.TubeSystem.TubeModel")) continue;
            var lighting = entity.AllComponents.Single(c => c.GetType().FullName == "Timberborn.Rendering.MaterialLightingRenderers");
            var paths = new Dictionary<MeshRenderer, string>();
            void Visit(Transform node, string path)
            {
                var i = 0;
                foreach (var renderer in node.GetComponents<MeshRenderer>())
                {
                    var key = path + ":renderer" + i++; paths.Add(renderer, key);
                    output.WriteLine(entity.EntityId + "\tshader\t" + key + "\t" + renderer.GetShaderUserValue()); rendererCount++;
                }
                for (var child = 0; child < node.childCount; child++)
                    Visit(node.GetChild(child), path + "/" + child + ":" + node.GetChild(child).name);
            }
            Visit(entity.Transform, "root");
            foreach (var field in new[] { "_renderers", "_disabledRenderers" })
            {
                var i = 0;
                foreach (MeshRenderer renderer in (IEnumerable)lighting.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lighting)!)
                    output.WriteLine(entity.EntityId + "\t" + field + "\t" + i++ + "\t" + paths[renderer]);
            }
            count++;
        }
        Debug.Log($"[T3MPTUBELIGHTING] entities={count} renderers={rendererCount}; exact shader user values and ordered native lighting membership");
    }
}
