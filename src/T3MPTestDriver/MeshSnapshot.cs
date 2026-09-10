using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Timberborn.EntitySystem;
using UnityEngine;

namespace T3MPTestDriver;

internal static class MeshSnapshot
{
    internal static string Digest(Mesh mesh)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void V3(Vector3 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        void V4(Vector4 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w); }
        void BoundsValue(Bounds b) { V3(b.center); V3(b.extents); }
        writer.Write(mesh.name); writer.Write(mesh.vertexCount); writer.Write((int)mesh.indexFormat);
        BoundsValue(mesh.bounds);
        var attributes = mesh.GetVertexAttributes(); writer.Write(attributes.Length);
        foreach (var a in attributes)
        { writer.Write((int)a.attribute); writer.Write((int)a.format); writer.Write(a.dimension); writer.Write(a.stream); }
        var vertices = mesh.vertices; writer.Write(vertices.Length); foreach (var v in vertices) V3(v);
        var normals = mesh.normals; writer.Write(normals.Length); foreach (var v in normals) V3(v);
        var tangents = mesh.tangents; writer.Write(tangents.Length); foreach (var v in tangents) V4(v);
        var colors = mesh.colors; writer.Write(colors.Length); foreach (var c in colors) V4(c);
        var uvs = new List<Vector4>();
        for (var channel = 0; channel < 8; channel++)
        { uvs.Clear(); mesh.GetUVs(channel, uvs); writer.Write(uvs.Count); foreach (var uv in uvs) V4(uv); }
        writer.Write(mesh.subMeshCount);
        for (var i = 0; i < mesh.subMeshCount; i++)
        {
            var submesh = mesh.GetSubMesh(i);
            writer.Write((int)submesh.topology); writer.Write(submesh.baseVertex);
            writer.Write(submesh.firstVertex); writer.Write(submesh.vertexCount);
            writer.Write(submesh.indexStart); writer.Write(submesh.indexCount); BoundsValue(submesh.bounds);
            var indices = mesh.GetIndices(i, false); writer.Write(indices.Length);
            foreach (var index in indices) writer.Write(index);
        }
        var poses = mesh.bindposes; writer.Write(poses.Length);
        foreach (var pose in poses) for (var i = 0; i < 16; i++) writer.Write(pose[i]);
        var weights = mesh.boneWeights; writer.Write(weights.Length);
        foreach (var weight in weights)
        {
            writer.Write(weight.boneIndex0); writer.Write(weight.boneIndex1); writer.Write(weight.boneIndex2); writer.Write(weight.boneIndex3);
            writer.Write(weight.weight0); writer.Write(weight.weight1); writer.Write(weight.weight2); writer.Write(weight.weight3);
        }
        writer.Write(mesh.blendShapeCount);
        if (mesh.blendShapeCount != 0)
        {
            var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                writer.Write(mesh.GetBlendShapeName(i)); var frames = mesh.GetBlendShapeFrameCount(i); writer.Write(frames);
                for (var frame = 0; frame < frames; frame++)
                {
                    writer.Write(mesh.GetBlendShapeFrameWeight(i, frame));
                    mesh.GetBlendShapeFrameVertices(i, frame, dv, dn, dt);
                    for (var v = 0; v < mesh.vertexCount; v++) { V3(dv[v]); V3(dn[v]); V3(dt[v]); }
                }
            }
        }
        writer.Flush(); stream.Position = 0;
        using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    internal static void Report(IEnumerable<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestMeshSnapshot")) return;
        var cache = new Dictionary<Mesh, string>();
        var rows = new List<string>(); var unreadable = 0;
        foreach (var entity in entities.OrderBy(e => e.EntityId))
        {
            foreach (var filter in entity.GameObject.GetComponentsInChildren<MeshFilter>(true))
                Record(entity, filter.transform, "MeshFilter", filter.sharedMesh);
            foreach (var renderer in entity.GameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Record(entity, renderer.transform, "SkinnedMeshRenderer", renderer.sharedMesh);
        }
        rows.Sort(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        var path = Path.Combine(directory, "mesh-state.tsv");
        File.WriteAllLines(path, new[] { "entity\tpath\tkind\tmeshDigest" }.Concat(rows));
        Debug.Log($"[T3MPMESHSTATE] references={rows.Count} uniqueMeshes={cache.Count} unreadable={unreadable}; entity meshes after load, not rendered pixels or future states");

        void Record(EntityComponent entity, Transform target, string kind, Mesh mesh)
        {
            var parts = new List<string>();
            for (var node = target; node != entity.Transform; node = node.parent)
                parts.Add(node.GetSiblingIndex() + ":" + node.name);
            parts.Reverse();
            var digest = "null";
            if (mesh && !cache.TryGetValue(mesh, out digest))
            {
                if (!mesh.isReadable) { digest = "unreadable:" + mesh.name; unreadable++; }
                else digest = Digest(mesh);
                cache.Add(mesh, digest);
            }
            rows.Add(entity.EntityId + "\t" + string.Join("/", parts) + "\t" + kind + "\t" + digest);
        }
    }
}
