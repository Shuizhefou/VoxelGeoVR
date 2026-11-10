using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoxelGeoVR.EditorTools
{
    public static class BuildScript
    {
        [MenuItem("VoxelGeoVR/Build All (Scene + Windows)")]
        public static void BuildAll()
        {
            SceneBuilder.BuildMainScene();

            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneBuilder.ScenePath },
                locationPathName = "Build/VoxelGeoVR.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log("[BuildScript] result=" + summary.result +
                      " size=" + summary.totalSize +
                      " errors=" + summary.totalErrors +
                      " time=" + summary.totalTime);

            if (Application.isBatchMode)
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        [MenuItem("VoxelGeoVR/Build Scene Only")]
        public static void BuildSceneOnly()
        {
            SceneBuilder.BuildMainScene();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
