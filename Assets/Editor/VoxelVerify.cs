using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VoxelGeoVR;

namespace VoxelGeoVR.EditorTools
{
    /// <summary>数据自检 + 离线渲染预览图（batchmode 用）。</summary>
    public static class VoxelVerify
    {
        public const string PreviewDir = "Preview";

        // ---------------------------------------------------------------- 自检
        [MenuItem("VoxelGeoVR/Self Test (data + mesh)")]
        public static void SelfTestMenu() { SelfTest(false); }

        public static void SelfTestBatch() { SelfTest(true); }

        static void SelfTest(bool exit)
        {
            int code = 0;
            try
            {
                var world = FindOrCreateWorld();
                if (!world.LoadSync()) throw new Exception("LoadSync failed: " + world.status);
                var man = world.Manifest;
                Debug.Log(string.Format("[SelfTest] manifest name={0} cells={1} chunks={2} spawn={3},{4},{5}",
                    man.name, man.cells.Length, SumChunks(man), man.spawn.block[0], man.spawn.block[1], man.spawn.block[2]));

                int loaded = world.LoadAllCellsSync();
                Debug.Log("[SelfTest] cells loaded " + loaded);

                int built = world.BuildAroundSync(new Vector3(man.spawn.block[0], man.spawn.block[1], man.spawn.block[2]), 4);
                Debug.Log(string.Format("[SelfTest] built chunks={0} verts={1} tris={2}", built, world.TotalVerts, world.TotalTris));

                var reportJson = VxFiles.ReadTextSync("report.json");
                var report = JsonUtility.FromJson<VxReport>(reportJson);
                if (report != null && report.summary != null)
                {
                    Debug.Log(string.Format("[SelfTest] report chunks={0} solid={1} faces={2} bytes={3}",
                        report.summary.chunks, report.summary.solidBlocks, report.summary.faces, report.summary.bytes));
                    if (report.summary.chunks != SumChunks(man))
                    {
                        Debug.LogError("[SelfTest] chunk count mismatch: manifest=" + SumChunks(man) + " report=" + report.summary.chunks);
                        code = 3;
                    }
                }
                if (built <= 0 || world.TotalTris <= 0)
                {
                    Debug.LogError("[SelfTest] no mesh built");
                    code = 4;
                }
                Debug.Log("[SelfTest] OK code=" + code);
            }
            catch (Exception e)
            {
                Debug.LogError("[SelfTest] EXCEPTION " + e);
                code = 2;
            }
            if (exit) EditorApplication.Exit(code);
        }

        static int SumChunks(VxWorldManifest man)
        {
            int n = 0;
            if (man.cells == null) return 0;
            for (int i = 0; i < man.cells.Length; i++) n += man.cells[i].chunks;
            return n;
        }

        // ---------------------------------------------------------------- 预览渲染
        [MenuItem("VoxelGeoVR/Render Voxel Preview")]
        public static void RenderMenu() { Render(false); }

        public static void RenderBatch() { Render(true); }

        /// <summary>建场景 + 出预览图（一次 batchmode 完成）。</summary>
        public static void BuildSceneAndPreview()
        {
            try
            {
                VoxelSceneBuilder.BuildVoxelScene();
            }
            catch (Exception e)
            {
                Debug.LogError("[VoxelVerify] scene build failed " + e);
                EditorApplication.Exit(5);
                return;
            }
            Render(true);
        }

        static void Render(bool exit)
        {
            int code = 0;
            try
            {
                var scene = EditorSceneManager.OpenScene(VoxelSceneBuilder.ScenePath, OpenSceneMode.Single);
                var world = UnityEngine.Object.FindObjectOfType<VoxelWorld>();
                if (world == null) throw new Exception("VoxelWorld not found in scene");
                if (!world.LoadSync()) throw new Exception("LoadSync failed: " + world.status);
                world.LoadAllCellsSync();
                world.showHud = false;
                RenderSettings.fog = false;

                var man = world.Manifest;
                var cam = Camera.main;
                if (cam == null) throw new Exception("Main Camera missing");

                Directory.CreateDirectory(PreviewDir);

                var spawn = man.spawn.block;
                var b = man.bounds;
                float centerX = (b.blockX[0] + b.blockX[1]) * 0.5f;
                float centerZ = (b.blockZ[0] + b.blockZ[1]) * 0.5f;
                int surfaceTop = b.surfaceYRange[1];

                Shot(world, cam, new Vector3(spawn[0] + 0.5f, spawn[1] + 4f, spawn[2] + 0.5f),
                     new Vector3(centerX, spawn[1] + 5f, centerZ), 8, "preview_ground.png");
                Shot(world, cam, new Vector3(spawn[0] + 0.5f, spawn[1] + 50f, spawn[2] + 40f),
                     new Vector3(spawn[0], spawn[1] - 5f, spawn[2] - 20f), 6, "preview_spawn_high.png");
                if (man.viewpoint != null && man.viewpoint.Length >= 3)
                {
                    Shot(world, cam, new Vector3(man.viewpoint[0], man.viewpoint[1], man.viewpoint[2]),
                         new Vector3(centerX, spawn[1], centerZ), 8, "preview_peak.png");
                }
                Shot(world, cam, new Vector3(centerX, surfaceTop + 40f, centerZ - 140f),
                     new Vector3(centerX, spawn[1], centerZ), 8, "preview_aerial.png");
                Shot(world, cam, new Vector3(b.blockX[0] + 30f, spawn[1] + 25f, b.blockZ[0] + 30f),
                     new Vector3(centerX, spawn[1], centerZ), 8, "preview_corner.png");
                Shot(world, cam, new Vector3(centerX, surfaceTop + 180f, centerZ + 0.01f),
                     new Vector3(centerX, 0f, centerZ), 10, "preview_topdown.png");

                Debug.Log(string.Format("[VoxelVerify] previews written, chunks={0} verts={1} tris={2}",
                    world.ChunkCount, world.TotalVerts, world.TotalTris));
            }
            catch (Exception e)
            {
                Debug.LogError("[VoxelVerify] EXCEPTION " + e);
                code = 2;
            }
            if (exit) EditorApplication.Exit(code);
        }

        static void Shot(VoxelWorld world, Camera cam, Vector3 pos, Vector3 lookAt, int radius, string file)
        {
            world.BuildAroundSync(pos, radius);
            var dir = lookAt - pos;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
            var rig = cam.transform.root;
            rig.SetPositionAndRotation(pos, rot);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;

            cam.farClipPlane = Mathf.Max(700f, radius * 16f * 3f);

            const int w = 1280, h = 720;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            var png = tex.EncodeToPNG();
            string path = Path.Combine(PreviewDir, file);
            File.WriteAllBytes(path, png);
            Debug.Log(string.Format("[VoxelVerify] shot {0} cam={1} want={2} parent={3} chunks={4} verts={5}",
                file, cam.transform.position.ToString("F1"), pos.ToString("F1"),
                cam.transform.parent != null ? cam.transform.parent.name : "-",
                world.ChunkCount, world.TotalVerts));
            cam.targetTexture = prev;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[VoxelVerify] wrote " + Path.GetFullPath(path));
        }

        static VoxelWorld FindOrCreateWorld()
        {
            var world = UnityEngine.Object.FindObjectOfType<VoxelWorld>();
            if (world != null) return world;
            var go = new GameObject("VoxelWorld");
            world = go.AddComponent<VoxelWorld>();
            world.autoStart = false;
            world.showHud = false;
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
            world.view = camGo.transform;
            world.voxelMaterial = VoxelSceneBuilder.GetOrCreateMaterial();
            return world;
        }
    }
}
