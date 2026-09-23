using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildHarness
{
    public static void Build()
    {
        var output = Environment.GetEnvironmentVariable("CONTINUUM_HARNESS_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || !output.EndsWith(".app", StringComparison.Ordinal))
            throw new Exception("Set CONTINUUM_HARNESS_OUTPUT to a new .app path");

        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const string scenePath = "Assets/GeneratedHarness.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        try
        {
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new Exception("Harness build failed: " + result.summary.result);
        }
        finally { AssetDatabase.DeleteAsset(scenePath); }
    }
}
