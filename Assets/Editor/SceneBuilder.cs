using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using VoxelGeoVR;

namespace VoxelGeoVR.EditorTools
{
    /// <summary>用代码创建默认场景（避免手写 .unity YAML）。
    /// 关键点：材质必须是"资产"（.mat），否则 shader 不会进包，手机上物体会不可见。</summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Main.unity";
        const string MaterialDir = "Assets/Materials";

        [MenuItem("VoxelGeoVR/Build Main Scene")]
        public static void BuildMainScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 平行光（移动端关闭实时阴影）
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // 地面
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            SetMaterial(ground, GetOrCreateMaterial("Ground", new Color(0.30f, 0.42f, 0.30f)));

            // 参照方块
            var positions = new[]
            {
                new Vector3(0f, 0.5f, 6f),
                new Vector3(3f, 1.0f, 9f),
                new Vector3(-4f, 1.5f, 8f),
                new Vector3(6f, 0.75f, 4f),
                new Vector3(-2f, 0.5f, 3f)
            };
            var cubeMat = GetOrCreateMaterial("Cube", new Color(0.55f, 0.62f, 0.75f));
            for (int i = 0; i < positions.Length; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "RefCube_" + i;
                cube.transform.position = positions[i];
                cube.transform.localScale = Vector3.one * (0.5f + 0.35f * i);
                SetMaterial(cube, cubeMat);
            }

            // 相机架 + 主相机
            var rigGo = new GameObject("CameraRig");
            rigGo.transform.position = new Vector3(0f, 1.6f, 0f);
            var rig = rigGo.AddComponent<CameraRig>();

            var camGo = new GameObject("Main Camera");
            camGo.transform.SetParent(rigGo.transform, false);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            rig.cameraTransform = camGo.transform;

            // 虚拟输入 + 屏幕按钮 + 自检
            var virtualInput = rigGo.AddComponent<VirtualInput>();
            rig.virtualInput = virtualInput;

            var ui = rigGo.AddComponent<OnScreenControls>();
            ui.rig = rig;
            ui.virtualInput = virtualInput;

            rigGo.AddComponent<SelfTestRunner>();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AddSceneToBuildSettings(ScenePath);

            Debug.Log("[SceneBuilder] Saved scene: " + ScenePath);
        }

        static void SetMaterial(GameObject go, Material mat)
        {
            if (mat == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
        }

        static Material GetOrCreateMaterial(string name, Color color)
        {
            Directory.CreateDirectory(MaterialDir);
            string path = MaterialDir + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = FindLitShader();
                if (shader == null)
                {
                    Debug.LogError("[SceneBuilder] URP Lit shader not found");
                    return null;
                }
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Shader FindLitShader()
        {
            var s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) return s;
            s = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader");
            if (s != null) return s;
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Packages/com.unity.render-pipelines.universal" });
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith("/Lit.shader")) return AssetDatabase.LoadAssetAtPath<Shader>(p);
            }
            return Shader.Find("Universal Render Pipeline/Unlit");
        }

        static void AddSceneToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == path))
            {
                scenes.Insert(0, new EditorBuildSettingsScene(path, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }
    }
}
