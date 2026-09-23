using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildShaderBundle
{
    public static void Build()
    {
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
        var paths = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/MetalShaders" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
            throw new Exception("Place matching-Unity shader sources under Assets/MetalShaders");
        foreach (var path in paths)
        {
            if (AssetDatabase.LoadAssetAtPath<Shader>(path) == null)
                throw new Exception("Unable to import shader " + path);
            AssetImporter.GetAtPath(path).assetBundleName = "continuum-metal-ui";
        }
        var output = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            throw new Exception("Set CONTINUUM_SHADER_BUNDLE_OUTPUT to an existing directory");
        var manifest = BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.None, BuildTarget.StandaloneOSX);
        if (manifest == null)
            throw new Exception("Shader bundle build failed");
        Debug.Log("Built Metal shader bundle with " + paths.Length + " shaders");
    }
}
