using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildT3MPShaderBundle
{
    private const string ShaderPath = "Assets/OfficialShaders/BotURP.shadergraph";
    private const string GeneratedShaderPath = "Assets/Generated/T3MPBotURP.shader";
    private const string PipelineAssetPath = "Assets/Generated/T3MPBuildURP.asset";
    private const string RendererAssetPath = "Assets/Generated/T3MPBuildRenderer.asset";
    private const string InstancedMaterialPath = "Assets/Generated/T3MPBotInstanced.mat";
    private const string InstancedBloomMaterialPath = "Assets/Generated/T3MPBotInstancedBloom.mat";
    private const string BundleName = "t3mp-bot-instancing";

    public static void Build()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        ConfigureBuildPipeline();
        GenerateReducedShader();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(GeneratedShaderPath);
        if (shader == null)
        {
            throw new InvalidOperationException("Generated BotURP shader did not import: " + GeneratedShaderPath);
        }
        CreateVariantReferenceMaterial(shader, InstancedMaterialPath, false);
        CreateVariantReferenceMaterial(shader, InstancedBloomMaterialPath, true);

        var output = Environment.GetEnvironmentVariable("T3MP_SHADER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("T3MP_SHADER_OUTPUT is not set.");
        }

        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[]
            {
                GeneratedShaderPath,
                InstancedMaterialPath,
                InstancedBloomMaterialPath
            }
        };
        var manifest = BuildPipeline.BuildAssetBundles(
            output,
            new[] { build },
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || !File.Exists(Path.Combine(output, BundleName)))
        {
            throw new InvalidOperationException("AssetBundle build failed.");
        }

        Debug.Log("[T3MP] Built instanced BotURP bundle: " + Path.Combine(output, BundleName));
    }

    public static void BuildSmokeTest()
    {
        const string smokeAssetPath = "Assets/Generated/T3MPBundleSmoke.txt";
        const string smokeBundleName = "t3mp-bundle-smoke";
        Directory.CreateDirectory(Path.GetDirectoryName(smokeAssetPath));
        File.WriteAllText(smokeAssetPath, "T3MP AssetBundle compatibility smoke test", new UTF8Encoding(false));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var output = Environment.GetEnvironmentVariable("T3MP_SHADER_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("T3MP_SHADER_OUTPUT is not set.");
        }

        Directory.CreateDirectory(output);
        var manifest = BuildPipeline.BuildAssetBundles(
            output,
            new[]
            {
                new AssetBundleBuild
                {
                    assetBundleName = smokeBundleName,
                    assetNames = new[] { smokeAssetPath }
                }
            },
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || !File.Exists(Path.Combine(output, smokeBundleName)))
        {
            throw new InvalidOperationException("Smoke-test AssetBundle build failed.");
        }

        Debug.Log("[T3MP] Built smoke-test AssetBundle: " + Path.Combine(output, smokeBundleName));
    }

    private static void CreateVariantReferenceMaterial(Shader shader, string path, bool bloom)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        material.enableInstancing = true;
        if (bloom)
        {
            material.EnableKeyword("_BLOOM_ENABLED");
        }
        else
        {
            material.DisableKeyword("_BLOOM_ENABLED");
        }
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
    }

    private static void ConfigureBuildPipeline()
    {
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
        if (renderer == null)
        {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, RendererAssetPath);
        }

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
        if (pipeline == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PipelineAssetPath));
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, PipelineAssetPath);
        }

        // Repair older generated assets created before the renderer was saved
        // as a persistent object. The serialized field is intentionally used
        // because URP exposes the default renderer only as an internal getter.
        var serializedPipeline = new SerializedObject(pipeline);
        var rendererDataList = serializedPipeline.FindProperty("m_RendererDataList");
        rendererDataList.arraySize = 1;
        rendererDataList.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
        serializedPipeline.FindProperty("m_DefaultRendererIndex").intValue = 0;
        serializedPipeline.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();

#pragma warning disable CS0618
        GraphicsSettings.defaultRenderPipeline = pipeline;
#pragma warning restore CS0618
        QualitySettings.renderPipeline = pipeline;
    }

    private static void GenerateReducedShader()
    {
        // ShaderGraphImporter exposes its generator internally. Reflection is
        // intentionally confined to this editor-only build helper so the
        // runtime mod has no UnityEditor or Shader Graph dependency.
        var importerType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter", false))
            .FirstOrDefault(type => type != null);
        var method = importerType?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return candidate.Name == "GetShaderText" &&
                       parameters.Length == 4 &&
                       parameters[3].IsOut;
            });
        if (method == null)
        {
            throw new MissingMethodException("ShaderGraphImporter.GetShaderText was not found.");
        }

        var arguments = new object[] { ShaderPath, null, null, null };
        var generated = method.Invoke(null, arguments) as string;
        if (string.IsNullOrWhiteSpace(generated) || !generated.Contains("Shader \"T3MP/BotURP\""))
        {
            throw new InvalidOperationException("BotURP Shader Graph generation failed.");
        }

        // The game decides its URP global keywords at runtime. Compiling every
        // possible lighting/fog/lightmap permutation into this AssetBundle
        // would generate ~1.8 million variants per stage. The bot replacement
        // is dynamic, non-lightmapped geometry, so retain only Shader Graph's
        // local material features and classic GPU-instancing permutation.
        var reduced = new StringBuilder(generated.Length);
        using (var reader = new StringReader(generated))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#pragma multi_compile", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("#pragma multi_compile_instancing", StringComparison.Ordinal) &&
                    !trimmed.EndsWith("_ _BLOOM_ENABLED", StringComparison.Ordinal))
                {
                    reduced.AppendLine("// T3MP stripped runtime-global variant: " + trimmed);
                }
                else
                {
                    reduced.AppendLine(line);
                }
            }
        }

        var outputDirectory = Path.GetDirectoryName(GeneratedShaderPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(GeneratedShaderPath, reduced.ToString(), new UTF8Encoding(false));
        Debug.Log($"[T3MP] Generated reduced BotURP shader: {GeneratedShaderPath} ({reduced.Length} chars)");
    }
}
