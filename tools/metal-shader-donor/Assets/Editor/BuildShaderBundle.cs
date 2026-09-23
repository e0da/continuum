using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildShaderBundle
{
    public static void Build()
    {
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/UI-Default.shader");
        if (shader == null || shader.name != "UI/Default")
            throw new Exception("Expected Unity's UI/Default shader at Assets/UI-Default.shader");

        AssetImporter.GetAtPath("Assets/UI-Default.shader").assetBundleName = "continuum-metal-ui";
        var output = Environment.GetEnvironmentVariable("CONTINUUM_SHADER_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            throw new Exception("Set CONTINUUM_SHADER_BUNDLE_OUTPUT to an existing directory");
        var manifest = BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.None, BuildTarget.StandaloneOSX);
        if (manifest == null)
            throw new Exception("Shader bundle build failed");
        Debug.Log("Built Metal UI/Default shader bundle");
    }
}
