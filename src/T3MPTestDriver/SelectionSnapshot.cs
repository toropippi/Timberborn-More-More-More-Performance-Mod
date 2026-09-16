using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Timberborn.EntitySystem;
using UnityEngine;

namespace T3MPTestDriver;

// Native selection geometry probe after load; no physics simulation step.
internal static class SelectionSnapshot
{
    internal static void Report(IEnumerable<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestSelectionSnapshot")) return;
        var ordered = entities.OrderBy(e => e.EntityId).ToArray();
        var owners = ordered.ToDictionary(e => e.Transform, e => e.EntityId.ToString());
        var index = Array.IndexOf(args, "-logFile");
        var path = Path.Combine(Path.GetDirectoryName(args[index + 1])!, "selection-state.tsv");
        var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        long rays = 0, hits = 0;
        Physics.SyncTransforms();
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            string V(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);
            string Identify(Transform transform)
            {
                var parts = new List<string>();
                while (transform)
                {
                    if (owners.TryGetValue(transform, out var id))
                    {
                        parts.Reverse(); return id + "/" + string.Join("/", parts);
                    }
                    parts.Add(transform.GetSiblingIndex() + ":" + transform.name);
                    transform = transform.parent;
                }
                parts.Reverse(); return "unowned/" + string.Join("/", parts);
            }
            foreach (var entity in ordered)
            {
                var center = entity.Transform.position + new Vector3(0.371f, 0.419f, 0.613f);
                for (var i = 0; i < directions.Length; i++)
                {
                    rays++;
                    var direction = directions[i];
                    var origin = center - direction * 128f;
                    // n10c has two overlapping finished colliders at exactly
                    // the same nearest distance on this ray. Single-hit tie
                    // selection varies in native reference runs too. Retain
                    // the full nearest candidate set as a regression fixture.
                    if (i == 4 && entity.EntityId.ToString() == "8d783cbf-e081-44bf-8f33-4fbb3741a078")
                    {
                        var candidates = Physics.RaycastAll(origin, direction, 256f);
                        if (candidates.Length == 0) throw new InvalidOperationException("Known selection tie has no hits");
                        var nearest = candidates.Min(h => h.distance);
                        var rows = candidates.Where(h => h.distance == nearest).Select(h => Identify(h.collider.transform) +
                            "\t" + F(h.distance) + "\t" + V(h.point) + "\t" + V(h.normal)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(path)!, "selection-tie-candidates.tsv"), rows);
                        Debug.Log("[T3MPSELECTION] nearestTieCandidates=" + rows.Length + " distance=" + F(nearest));
                    }
                    writer.Write(entity.EntityId + "\t" + i + "\t" + V(origin) + "\t");
                    if (Physics.Raycast(origin, direction, out var hit, 256f))
                    {
                        hits++;
                        writer.WriteLine(Identify(hit.collider.transform) + "\t" + F(hit.distance) + "\t" + V(hit.point) + "\t" + V(hit.normal));
                    }
                    else writer.WriteLine("miss");
                }
            }
        }
        using var sha = SHA256.Create();
        using var file = File.OpenRead(path);
        var hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        Debug.Log($"[T3MPSELECTION] rays={rays} hits={hits} sha256={hash}; post-load diagnostic");
    }
}
