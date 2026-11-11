using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VoxelGeoVR;

namespace VoxelGeoVR.EditorTools
{
    /// <summary>生成体素漫游场景 Assets/Scenes/Voxel.unity。</summary>
    public static class VoxelSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Voxel.unity";
        public const string MaterialPath = "Assets/Materials/Voxel.mat";
        public const string DoubleSidedMaterialPath = "Assets/Materials/VoxelDoubleSided.mat";
        public const string EnvelopeMaterialPath = "Assets/Materials/VoxelEnvelope.mat";
        public const string SectionCapMaterialPath = "Assets/Materials/VoxelSectionCap.mat";
        public const string ShaderName = "VoxelGeoVR/VoxelVertexColor";

        [MenuItem("VoxelGeoVR/Build Voxel Scene")]
        public static void BuildVoxelScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 光照：单方向光，移动端关阴影
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            // 材质资产（必须是 .mat，否则 shader 不进包）
            var mat = GetOrCreateMaterial();
            var doubleMat = GetOrCreateDoubleSidedMaterial();
            var envelopeMat = GetOrCreateEnvelopeMaterial();
            var sectionCapMat = GetOrCreateSectionCapMaterial();

            // 相机架
            var rigGo = new GameObject("CameraRig");
            rigGo.transform.position = new Vector3(0f, 80f, 0f);
            var rig = rigGo.AddComponent<CameraRig>();
            rig.mode = 1;               // 手机上默认走屏幕按钮

            var camGo = new GameObject("Main Camera");
            camGo.transform.SetParent(rigGo.transform, false);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            rig.cameraTransform = camGo.transform;

            var virtualInput = rigGo.AddComponent<VirtualInput>();
            rig.virtualInput = virtualInput;
            var ui = rigGo.AddComponent<OnScreenControls>();
            ui.rig = rig;
            ui.virtualInput = virtualInput;

            // 体素世界
            var worldGo = new GameObject("VoxelWorld");
            var world = worldGo.AddComponent<VoxelWorld>();
            world.voxelMaterial = mat;
            world.envelopeMaterial = envelopeMat;
            world.sectionCapMaterial = sectionCapMat;
            world.view = camGo.transform;
            world.autoStart = true;
            world.showHud = true;
            world.streamRadius = 4;
            world.band = 0;          // 全高度提取，不做地表裁剪

            // 阶段 4：显示交互控制器（A 虚化 / B 剖面 / C 筛选）。
            // VoxelWorld.Awake 会兜底自动挂一个，这里复用，避免重复组件。
            var display = worldGo.GetComponent<VoxelDisplayController>();
            if (display == null) display = worldGo.AddComponent<VoxelDisplayController>();
            display.world = world;
            display.sourceCamera = cam;
            display.baseMaterial = mat;
            display.doubleSidedMaterial = doubleMat;

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AddSceneToBuildSettings(ScenePath);
            Debug.Log("[VoxelSceneBuilder] saved " + ScenePath + " material=" + (mat != null ? mat.name : "NULL"));
        }

        public static Material GetOrCreateMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/VoxelVertexColor.shader");
                if (shader == null)
                {
                    Debug.LogError("[VoxelSceneBuilder] voxel shader not found, fallback to URP/Unlit");
                    shader = Shader.Find("Universal Render Pipeline/Unlit");
                }
                Directory.CreateDirectory("Assets/Materials");
                mat = new Material(shader) { name = "Voxel" };
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>剖面展示框用双面材质（Cull Off）。与主材质共用同一 shader，只改 _CullMode。</summary>
        public static Material GetOrCreateDoubleSidedMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(DoubleSidedMaterialPath);
            if (mat == null)
            {
                var baseMat = GetOrCreateMaterial();
                var shader = baseMat != null ? baseMat.shader : Shader.Find(ShaderName);
                if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/VoxelVertexColor.shader");
                Directory.CreateDirectory("Assets/Materials");
                mat = new Material(shader) { name = "VoxelDoubleSided" };
                mat.SetFloat("_CullMode", 0f);
                AssetDatabase.CreateAsset(mat, DoubleSidedMaterialPath);
            }
            mat.SetFloat("_CullMode", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>A 功能岩层包络材质：双面 + 不参与虚化。</summary>
        public static Material GetOrCreateEnvelopeMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(EnvelopeMaterialPath);
            if (mat == null)
            {
                var baseMat = GetOrCreateMaterial();
                var shader = baseMat != null ? baseMat.shader : Shader.Find(ShaderName);
                if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/VoxelVertexColor.shader");
                Directory.CreateDirectory("Assets/Materials");
                mat = new Material(shader) { name = "VoxelEnvelope" };
                AssetDatabase.CreateAsset(mat, EnvelopeMaterialPath);
            }
            mat.SetFloat("_CullMode", 0f);
            mat.SetFloat("_NoFade", 1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>剖面盖面材质：不参与虚化，也不要被剖面 clip 裁掉。</summary>
        public static Material GetOrCreateSectionCapMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SectionCapMaterialPath);
            if (mat == null)
            {
                var baseMat = GetOrCreateMaterial();
                var shader = baseMat != null ? baseMat.shader : Shader.Find(ShaderName);
                if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/VoxelVertexColor.shader");
                Directory.CreateDirectory("Assets/Materials");
                mat = new Material(shader) { name = "VoxelSectionCap" };
                AssetDatabase.CreateAsset(mat, SectionCapMaterialPath);
            }
            mat.SetFloat("_CullMode", 0f);
            mat.SetFloat("_NoFade", 1f);
            mat.SetFloat("_SectionCap", 1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void AddSceneToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(s => s.path == path))
                scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            else
            {
                int i = scenes.FindIndex(s => s.path == path);
                scenes[i] = new EditorBuildSettingsScene(path, true);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
