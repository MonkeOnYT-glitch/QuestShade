using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildBundles
{
    public static void Build()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Vulkan });

        var shaders = new[] { "Assets/QuestShade/QSFinalBlit.shader", "Assets/QuestShade/QSScreen.shader", "Assets/QuestShade/QSYUV.shader", "Assets/QuestShade/QSPost.shader", "Assets/QuestShade/QSBody.shader", "Assets/QuestShade/QSOutline.shader", "Assets/QuestShade/QSShadowDepth.shader" };
        foreach (var shader in shaders)
        {
            AssetDatabase.ImportAsset(shader, ImportAssetOptions.ForceUpdate);
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(shader);
            if (sh == null || ShaderUtil.ShaderHasError(sh)) { Debug.LogError("[QS] shader has errors: " + shader); EditorApplication.Exit(2); return; }
        }
        var names = new System.Collections.Generic.List<string>(shaders);
        const string handheld = "Assets/QuestShade/HandheldCam/QSHandheld.prefab";
        if (File.Exists(handheld)) names.Add(handheld);
        var build = new[] { new AssetBundleBuild { assetBundleName = "questshade", assetNames = names.ToArray() } };
        foreach (var (target, dir) in new[] { (BuildTarget.Android, "android") })
        {
            var outDir = Path.Combine("C:/QSBuild/bundles", dir);
            Directory.CreateDirectory(outDir);
            var m = BuildPipeline.BuildAssetBundles(outDir, build, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, target);
            Debug.Log($"[QS] {target}: {(m != null ? "OK" : "FAILED")}");
            if (m == null) EditorApplication.Exit(1);
        }
        EditorApplication.Exit(0);
    }
}
