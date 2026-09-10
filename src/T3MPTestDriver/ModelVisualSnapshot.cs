using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Timberborn.EntitySystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace T3MPTestDriver;

// Post-load diagnostic only. Static tube/path hierarchies, including inactive
// candidates, renderer settings and shared material shader properties.
internal static class ModelVisualSnapshot
{
    internal static void Report(IEnumerable<EntityComponent> entities)
    {
        var args = Environment.GetCommandLineArgs();
        var construction = args.Contains("-t3mpTestConstructionVisualSnapshot");
        var allModels = args.Contains("-t3mpTestAllModelVisualSnapshot");
        if (!args.Contains("-t3mpTestModelVisualSnapshot") && !construction && !allModels) return;
        var logIndex = Array.IndexOf(args, "-logFile");
        var directory = Path.GetDirectoryName(args[logIndex + 1])!;
        using var detail = new StreamWriter(Path.Combine(directory, "model-visual-detail.txt"), false, new UTF8Encoding(false));
        using var rows = new StreamWriter(Path.Combine(directory, "model-visual-state.tsv"), false, new UTF8Encoding(false));
        using var sha = SHA256.Create();
        var combined = new StringBuilder();
        long count = 0, nodes = 0, renderers = 0;
        foreach (var entity in entities.OrderBy(e => e.EntityId))
        {
            if (!allModels && !entity.AllComponents.Any(c => c.GetType().FullName is "Timberborn.TubeSystem.TubeModel" or "Timberborn.PathSystem.DynamicPathModel" ||
                (construction && c.GetType().FullName == "Timberborn.ConstructionSites.ConstructionSiteProgressVisualizer"))) continue;
            var state = new StringBuilder();
            void F(float value) => state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            void V(Vector4 v) { F(v.x); F(v.y); F(v.z); F(v.w); }
            void Visit(Transform transform, string path)
            {
                nodes++;
                var go = transform.gameObject;
                state.Append(path).Append('|').Append(go.name).Append('|').Append(go.activeSelf).Append('|').Append(go.activeInHierarchy).Append('|');
                V(transform.localPosition); var q = transform.localRotation; V(new Vector4(q.x, q.y, q.z, q.w)); V(transform.localScale);
                state.AppendLine();
                // Visibility toggling affects all collider types, not only boxes.
                foreach (var collider in go.GetComponents<Collider>())
                    state.Append("collider-state|").Append(collider.GetType().FullName).Append('|')
                        .Append(collider.enabled).Append('|').Append(collider.isTrigger).AppendLine();
                foreach (var collider in go.GetComponents<BoxCollider>())
                {
                    state.Append("boxCollider|").Append(collider.enabled).Append('|').Append(collider.isTrigger).Append('|')
                        .Append(go.layer).Append('|').Append(collider.attachedRigidbody ? "rigidBody" : "static").Append('|');
                    V(collider.center); V(collider.size); F(collider.contactOffset);
                    state.AppendLine();
                }
                foreach (var renderer in go.GetComponents<Renderer>())
                {
                    renderers++;
                    state.Append("renderer|").Append(renderer.GetType().FullName).Append('|').Append(renderer.enabled).Append('|')
                        .Append(renderer.shadowCastingMode).Append('|').Append(renderer.receiveShadows).AppendLine();
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (!material) { state.AppendLine("null material"); continue; }
                        var shader = material.shader;
                        state.Append("material|").Append(material.name).Append('|').Append(shader.name).Append('|').Append(material.renderQueue).Append('|')
                            .Append(string.Join(",", material.shaderKeywords.OrderBy(s => s, StringComparer.Ordinal))).AppendLine();
                        for (var i = 0; i < shader.GetPropertyCount(); i++)
                        {
                            var name = shader.GetPropertyName(i);
                            state.Append(name).Append('=');
                            switch (shader.GetPropertyType(i))
                            {
                                case ShaderPropertyType.Color: V(material.GetColor(name)); break;
                                case ShaderPropertyType.Vector: V(material.GetVector(name)); break;
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range: F(material.GetFloat(name)); break;
                                case ShaderPropertyType.Int: state.Append(material.GetInteger(name)); break;
                                case ShaderPropertyType.Texture:
                                    var texture = material.GetTexture(name); state.Append(texture ? texture.name : "null").Append('|');
                                    V(material.GetTextureScale(name)); V(material.GetTextureOffset(name)); break;
                            }
                            state.AppendLine();
                        }
                    }
                }
                for (var i = 0; i < transform.childCount; i++) Visit(transform.GetChild(i), path + "/" + i);
            }
            Visit(entity.Transform, "root");
            var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(state.ToString()))).Replace("-", "");
            var row = entity.EntityId + "\t" + hash;
            rows.WriteLine(row); combined.Append(row).Append('\n');
            detail.WriteLine("ENTITY " + entity.EntityId); detail.Write(state.ToString()); count++;
        }
        var total = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString()))).Replace("-", "");
        Debug.Log($"[T3MPVISUAL] entities={count} nodes={nodes} renderers={renderers} sha256={total}");
    }
}
