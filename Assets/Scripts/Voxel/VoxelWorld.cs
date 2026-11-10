using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>
    /// 体素世界：读 StreamingAssets/vxworld 的 manifest + cells，按相机距离流式加载，
    /// 每 chunk 生成一个 Mesh + GameObject。
    /// </summary>
    public class VoxelWorld : MonoBehaviour
    {
        [Header("引用")]
        public Material voxelMaterial;
        public Transform view;

        [Header("参数")]
        public int streamRadius = 4;     // 可见半径（chunk）
        public int band = 8;             // 地表以下保留层数
        public bool autoStart = true;
        public bool showHud = true;

        [Header("状态")]
        public string status = "boot";

        VxWorldManifest manifest;
        VoxelPalette palette;

        readonly Dictionary<Vector2Int, VxCell> cells = new Dictionary<Vector2Int, VxCell>();
        readonly Dictionary<Vector2Int, GameObject> live = new Dictionary<Vector2Int, GameObject>();
        readonly List<Vector2Int> scratch = new List<Vector2Int>();

        readonly List<Vector3> verts = new List<Vector3>(8192);
        readonly List<Vector3> normals = new List<Vector3>(8192);
        readonly List<Color32> colors = new List<Color32>(8192);
        readonly List<int> tris = new List<int>(24000);

        public int ChunkCount { get { return live.Count; } }
        public int CellCount { get { return cells.Count; } }
        public int TotalVerts { get; private set; }
        public int TotalTris { get; private set; }
        public VxWorldManifest Manifest { get { return manifest; } }

        class TextResult { public string text; public string error; }
        class BytesResult { public byte[] data; public string error; }

        void Awake()
        {
            if (view == null && Camera.main != null) view = Camera.main.transform;
        }

        void Start()
        {
            if (autoStart) StartCoroutine(Boot());
        }

        Vector3 ViewPos()
        {
            if (view == null) view = Camera.main != null ? Camera.main.transform : transform;
            return view != null ? view.position : Vector3.zero;
        }

        IEnumerator Boot()
        {
            status = "读取 world.manifest.json";
            var tr = new TextResult();
            yield return VxFiles.ReadText("world.manifest.json", s => tr.text = s, e => tr.error = e);
            if (tr.text == null)
            {
                status = "manifest 读取失败: " + tr.error;
                Debug.LogError("[VoxelWorld] " + status);
                yield break;
            }
            ApplyManifest(tr.text);

            status = "读取 blocks.json";
            var br = new TextResult();
            yield return VxFiles.ReadText(manifest.blocks ?? "blocks.json", s => br.text = s, e => br.error = e);
            if (br.text == null)
            {
                status = "blocks 读取失败: " + br.error;
                Debug.LogError("[VoxelWorld] " + status);
                yield break;
            }
            ApplyBlocks(br.text);
            ApplyEnvironment();
            TeleportToSpawn();
            status = "流式加载中";
            Debug.Log(string.Format("[VoxelWorld] {0}  chunks={1}  cell={2}  band={3}  radius={4}",
                manifest.name, TotalManifestChunks(), manifest.cellSize, band, streamRadius));
            yield return StreamLoop();
        }

        void ApplyManifest(string json)
        {
            manifest = JsonUtility.FromJson<VxWorldManifest>(json);
            if (manifest == null || manifest.cells == null) throw new System.Exception("manifest 解析失败");
        }

        void ApplyBlocks(string json)
        {
            var doc = JsonUtility.FromJson<VxBlocksDoc>(json);
            palette = new VoxelPalette(doc);
            Debug.Log("[VoxelWorld] palette blocks=" + palette.colors.Length);
        }

        int TotalManifestChunks()
        {
            int n = 0;
            if (manifest != null && manifest.cells != null)
                for (int i = 0; i < manifest.cells.Length; i++) n += manifest.cells[i].chunks;
            return n;
        }

        void ApplyEnvironment()
        {
            // 移动端默认 30fps，这里显式锁 60（阶段 4 再按 PerfDog 结果调）
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;

            var sky = new Color(0.53f, 0.72f, 0.91f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = sky;
            RenderSettings.fogStartDistance = streamRadius * 16f * 0.55f;
            RenderSettings.fogEndDistance = streamRadius * 16f * 1.05f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);

            var cam = view != null ? view.GetComponent<Camera>() : Camera.main;
            if (cam == null && view != null) cam = view.GetComponentInChildren<Camera>();
            if (cam != null)
            {
                cam.farClipPlane = Mathf.Max(200f, streamRadius * 16f * 2f);
                cam.nearClipPlane = 0.1f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = sky;
            }
        }

        public void TeleportToSpawn()
        {
            if (manifest == null) return;
            Vector3 p;
            if (manifest.spawnView != null && manifest.spawnView.Length >= 3)
            {
                // 工具挑的"出生点附近开阔平地"，避免第一眼就卡在树干里
                p = new Vector3(manifest.spawnView[0], manifest.spawnView[1], manifest.spawnView[2]);
            }
            else if (manifest.spawn != null && manifest.spawn.block != null && manifest.spawn.block.Length >= 3)
            {
                // MC 出生点（湖岸/平原），第一眼就有地景
                var b = manifest.spawn.block;
                p = new Vector3(b[0] + 0.5f, b[1] + 2.2f, b[2] + 0.5f);
            }
            else if (manifest.viewpoint != null && manifest.viewpoint.Length >= 3)
            {
                p = new Vector3(manifest.viewpoint[0], manifest.viewpoint[1], manifest.viewpoint[2]);
            }
            else
            {
                return;
            }
            if (view == null) return;
            // 相机是 CameraRig 的子物体：必须移动根节点并把相机的局部位置归零，
            // 否则相机相对父节点会留下一个巨大偏移（表现为镜头跑到地图外/地下）。
            var root = view.root;
            root.position = p;
            view.localPosition = Vector3.zero;
            if (manifest.bounds != null && manifest.bounds.chunkX != null && manifest.bounds.chunkX.Length >= 2)
            {
                float cx = (manifest.bounds.chunkX[0] + manifest.bounds.chunkX[1]) * 8f + 8f;
                float cz = (manifest.bounds.chunkZ[0] + manifest.bounds.chunkZ[1]) * 8f + 8f;
                Vector3 dir = new Vector3(cx, p.y, cz) - p;
                var flat = new Vector3(dir.x, 0f, dir.z);
                if (flat.sqrMagnitude > 1f) root.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
            }
        }

        IEnumerator StreamLoop()
        {
            while (true)
            {
                Vector3 p = ViewPos();
                int ccx = Mathf.FloorToInt(p.x / 16f);
                int ccz = Mathf.FloorToInt(p.z / 16f);

                // 1) 补 cell（一次一个）
                VxCellInfo want = null;
                float best = float.MaxValue;
                var list = manifest.cells;
                for (int i = 0; i < list.Length; i++)
                {
                    var info = list[i];
                    if (cells.ContainsKey(new Vector2Int(info.cellX, info.cellZ))) continue;
                    float d = CellDistance(info, ccx, ccz);
                    if (d <= streamRadius + 10f && d < best) { best = d; want = info; }
                }
                if (want != null)
                {
                    yield return LoadCell(want);
                    continue;
                }

                // 2) 建 chunk（每帧最多 2 个，优先最近的）
                int budget = 2;
                while (budget-- > 0)
                {
                    VxCell bestCell = null;
                    VxCell.ChunkEntry bestEntry = default(VxCell.ChunkEntry);
                    float bestD = float.MaxValue;
                    foreach (var kv in cells)
                    {
                        var cell = kv.Value;
                        for (int i = 0; i < cell.entries.Length; i++)
                        {
                            var e = cell.entries[i];
                            int cx = cell.cellX * 8 + e.cx;
                            int cz = cell.cellZ * 8 + e.cz;
                            if (live.ContainsKey(new Vector2Int(cx, cz))) continue;
                            float d = (cx - ccx) * (cx - ccx) + (cz - ccz) * (cz - ccz);
                            if (d > streamRadius * streamRadius) continue;
                            if (d < bestD) { bestD = d; bestCell = cell; bestEntry = e; }
                        }
                    }
                    if (bestCell == null) break;
                    CreateChunkObject(VxCellReader.DecodeChunk(bestCell, bestEntry));
                    yield return null;
                }

                // 3) 卸载远处 chunk
                scratch.Clear();
                foreach (var kv in live)
                {
                    int dx = kv.Key.x - ccx, dz = kv.Key.y - ccz;
                    if (dx * dx + dz * dz > (streamRadius + 2) * (streamRadius + 2))
                        scratch.Add(kv.Key);
                }
                for (int i = 0; i < scratch.Count; i++) DestroyChunk(scratch[i]);

                // 4) 卸载远处 cell
                scratch.Clear();
                foreach (var kv in cells)
                {
                    int cx = kv.Key.x * 8 + 4, cz = kv.Key.y * 8 + 4;
                    int dx = cx - ccx, dz = cz - ccz;
                    if (dx * dx + dz * dz > (streamRadius + 18) * (streamRadius + 18))
                        scratch.Add(kv.Key);
                }
                for (int i = 0; i < scratch.Count; i++)
                {
                    if (HasLiveChunkOfCell(scratch[i])) continue;
                    cells.Remove(scratch[i]);
                }

                yield return null;
            }
        }

        bool HasLiveChunkOfCell(Vector2Int cellKey)
        {
            foreach (var kv in live)
            {
                if (Mathf.FloorToInt(kv.Key.x / 8f) == cellKey.x && Mathf.FloorToInt(kv.Key.y / 8f) == cellKey.y)
                    return true;
            }
            return false;
        }

        float CellDistance(VxCellInfo info, int ccx, int ccz)
        {
            int dx = 0, dz = 0;
            if (ccx < info.chunkMinX) dx = info.chunkMinX - ccx;
            else if (ccx > info.chunkMaxX) dx = ccx - info.chunkMaxX;
            if (ccz < info.chunkMinZ) dz = info.chunkMinZ - ccz;
            else if (ccz > info.chunkMaxZ) dz = ccz - info.chunkMaxZ;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        IEnumerator LoadCell(VxCellInfo info)
        {
            status = "加载 " + info.file;
            var r = new BytesResult();
            yield return VxFiles.ReadBytes(info.file, b => r.data = b, e => r.error = e);
            if (r.data == null)
            {
                Debug.LogError("[VoxelWorld] cell 读取失败 " + info.file + " : " + r.error);
                yield break;
            }
            try
            {
                var cell = VxCellReader.ParseCell(r.data);
                cells[new Vector2Int(info.cellX, info.cellZ)] = cell;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VoxelWorld] cell 解析失败 " + info.file + " : " + e.Message);
            }
        }

        void CreateChunkObject(VxChunk chunk)
        {
            var key = new Vector2Int(chunk.chunkX, chunk.chunkZ);
            var go = new GameObject("chunk_" + chunk.chunkX + "_" + chunk.chunkZ);
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(chunk.chunkX * 16f, 0f, chunk.chunkZ * 16f);

            var mesh = new Mesh { name = go.name };
            ChunkMesher.BuildInto(mesh, chunk, palette, band, verts, normals, colors, tris);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = voxelMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            live[key] = go;
            TotalVerts += mesh.vertexCount;
            TotalTris += tris.Count / 3;
        }

        void DestroyChunk(Vector2Int key)
        {
            GameObject go;
            if (!live.TryGetValue(key, out go)) return;
            live.Remove(key);
            if (go != null)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    TotalVerts -= mf.sharedMesh.vertexCount;
                    TotalTris -= mf.sharedMesh.triangles.Length / 3;
                    Destroy(mf.sharedMesh);
                }
                Destroy(go);
            }
        }

        // ---------------------------------------------------------- 同步接口（编辑器预览/自检）
        public bool LoadSync()
        {
            try
            {
                ApplyManifest(VxFiles.ReadTextSync("world.manifest.json"));
                ApplyBlocks(VxFiles.ReadTextSync(manifest.blocks ?? "blocks.json"));
                ApplyEnvironment();
                TeleportToSpawn();
                return true;
            }
            catch (System.Exception e)
            {
                status = "LoadSync 失败: " + e.Message;
                Debug.LogError("[VoxelWorld] " + status);
                return false;
            }
        }

        public int LoadAllCellsSync()
        {
            if (manifest == null) return 0;
            int n = 0;
            for (int i = 0; i < manifest.cells.Length; i++)
            {
                var info = manifest.cells[i];
                var key = new Vector2Int(info.cellX, info.cellZ);
                if (cells.ContainsKey(key)) continue;
                try
                {
                    cells[key] = VxCellReader.ParseCell(VxFiles.ReadBytesSync(info.file));
                    n++;
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[VoxelWorld] cell " + info.file + " : " + e.Message);
                }
            }
            return n;
        }

        public int BuildAroundSync(Vector3 pos, int radius, int maxChunks = 4096)
        {
            if (palette == null) return 0;
            int ccx = Mathf.FloorToInt(pos.x / 16f);
            int ccz = Mathf.FloorToInt(pos.z / 16f);
            int built = 0;
            var todo = new List<KeyValuePair<float, VxChunk>>();
            foreach (var kv in cells)
            {
                var cell = kv.Value;
                for (int i = 0; i < cell.entries.Length; i++)
                {
                    var e = cell.entries[i];
                    int cx = cell.cellX * 8 + e.cx, cz = cell.cellZ * 8 + e.cz;
                    float d = (cx - ccx) * (cx - ccx) + (cz - ccz) * (cz - ccz);
                    if (d > radius * radius) continue;
                    var key = new Vector2Int(cx, cz);
                    if (live.ContainsKey(key)) continue;
                    todo.Add(new KeyValuePair<float, VxChunk>(d, VxCellReader.DecodeChunk(cell, e)));
                }
            }
            todo.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < todo.Count && built < maxChunks; i++)
            {
                CreateChunkObject(todo[i].Value);
                built++;
            }
            return built;
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(16, Screen.height / 48) };
            style.normal.textColor = Color.white;
            var p = ViewPos();
            string text = string.Format(
                "VoxelGeoVR  {0}\nchunks {1}   cells {2}   verts {3}\ntris {4}   band {5}   R {6}\npos ({7:F0}, {8:F0}, {9:F0})   fps {10:F0}\n{11}",
                manifest != null ? manifest.name : "-", live.Count, cells.Count, TotalVerts,
                TotalTris, band, streamRadius, p.x, p.y, p.z, 1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f),
                status);
            GUI.Label(new Rect(16, 120, 1100, 220), text, style);
        }
    }
}
