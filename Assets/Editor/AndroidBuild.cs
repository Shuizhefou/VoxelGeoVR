using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using VoxelGeoVR.EditorTools;

/// <summary>
/// Android APK 打包入口。
/// 菜单：VoxelGeoVR/Build Android APK
/// 命令行：
///   Unity.exe -batchmode -nographics -projectPath "E:\unity\Projects\VoxelGeoVR"
///             -executeMethod AndroidBuild.BuildApk -logFile <log>
/// </summary>
public static class AndroidBuild
{
    private const string ScenePath = "Assets/Scenes/Main.unity";
    private const string ApkPath = "Build/VoxelGeoVR.apk";

    [MenuItem("VoxelGeoVR/Build Android APK")]
    public static void BuildApk()
    {
        Configure();

        // 每次打包都重建场景 + 材质资产，确保 shader 被打进包
        SceneBuilder.BuildMainScene();

        Directory.CreateDirectory("Build");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log($"[AndroidBuild] result={summary.result} size={summary.totalSize} " +
                  $"errors={summary.totalErrors} warnings={summary.totalWarnings} time={summary.totalTime}");

        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static void Configure()
    {
        var target = NamedBuildTarget.Android;

        PlayerSettings.SetApplicationIdentifier(target, "com.voxelgeo.vr");

        PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        PlayerSettings.Android.useCustomKeystore = false;

        AssetDatabase.SaveAssets();
        Debug.Log("[AndroidBuild] PlayerSettings configured: com.voxelgeo.vr / IL2CPP / ARM64 / minSdk 25 / Landscape");
    }
}
