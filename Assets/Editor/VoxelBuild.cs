using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoxelGeoVR.EditorTools
{
    /// <summary>体素场景的 Android APK 打包入口。
    /// 菜单：VoxelGeoVR/Build Voxel APK
    /// 命令行：Unity.exe -batchmode -nographics -projectPath ... -executeMethod VoxelGeoVR.EditorTools.VoxelBuild.BuildApk
    /// </summary>
    public static class VoxelBuild
    {
        public const string ApkPath = "Build/VoxelGeoVR_v1.apk";

        [MenuItem("VoxelGeoVR/Build Voxel APK")]
        public static void BuildApk()
        {
            Configure();
            VoxelSceneBuilder.BuildVoxelScene();
            Directory.CreateDirectory("Build");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { VoxelSceneBuilder.ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log(string.Format("[VoxelBuild] result={0} size={1} errors={2} warnings={3} time={4}",
                s.result, s.totalSize, s.totalErrors, s.totalWarnings, s.totalTime));
            EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        static void Configure()
        {
            var target = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(target, "com.voxelgeo.vr");
            PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.Android.useCustomKeystore = false;
            AssetDatabase.SaveAssets();
            Debug.Log("[VoxelBuild] PlayerSettings configured");
        }
    }
}
