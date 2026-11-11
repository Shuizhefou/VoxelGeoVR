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
        public Material envelopeMaterial;
        public Transform view;

        [Header("参数")]
        public int streamRadius = 4;     // 可见半径（chunk）
        public int band = 0;             // 0 = 全高度（不裁剪）；>0 = 只保留地表以下 band 层
        public bool autoStart = true;
        public bool showHud = true;

        [Header("状态")]
        public string status = "boot";

        [Header("显示交互")]
        public VoxelDisplayController display;

        VxWorldManifest manifest;
        VoxelPalette palette;

        readonly Dictionary<Vector2Int, VxCell> cells = new Dictionary<Vector2Int, VxCell>();
        readonly Dictionary<Vector2Int, GameObject> live = new Dictionary<Vector2Int, GameObject>();
        readonly Dictionary<Vector2Int, VxChunk> chunkData = new Dictionary<Vector2Int, VxChunk>();
        readonly List<Vector2Int> scratch = new List<Vector2Int>();
        readonly List<Vector2Int> pruneScratch = new List<Vector2Int>();

        readonly List<Vector3> verts = new List<Vector3>(8192);
        readonly List<Vector3> normals = new List<Vector3>(8192);
        readonly List<Color32> colors = new List<Color32>(8192);
        readonly List<int> tris = new List<int>(24000);

        bool[] currentVisibility;      // null = 所有 solid 方块可见
        Coroutine rebuildRoutine;

        GameObject envelopeGo;
        Mesh envelopeMesh;
        MeshRenderer envelopeMr;
        ushort[] envelopeVolume;
        bool envelopeDirty = true;
        bool envelopeWasOn;
        float envelopeLastRadius = -1f;
        Vector3Int envelopeLastBlock = new Vector3Int(999999, 999999, 999999);

        public int ChunkCount { get { return live.Count; } }
        public int CellCount { get { return cells.Count; } }
        public int TotalVerts { get; private set; }
        public int TotalTris { get; private set; }
        public VxWorldManifest Manifest { get { return manifest; } }
        public VoxelPalette Palette { get { return palette; } }

        public bool FilterRebuilding { get; private set; }
        public int FilterRebuildDone { get; private set; }
        public int FilterRebuildTotal { get; private set; }
        public string FilterStatus { get; private set; } = "全部方块可见";

        Vector3 anchorPos;
        int anchorFrames;

        class TextResult { public string text; public string error; }
        class BytesResult { public byte[] data; public string error; }

        void Awake()
        {
            if (view == null && Camera.main != null) view = Camera.main.transform;

            // 显示交互控制器由场景生成器挂载；旧场景（v0.3.0）在这里兜底自动补上。
            display = GetComponent<VoxelDisplayController>();
            if (display == null) display = gameObject.AddComponent<VoxelDisplayController>();
            if (display != null) display.Bind(this);
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
            anchorPos = p;
            anchorFrames = 120;      // 头 2 秒每帧钉住，避免任何输入/漂移把相机带到地下
            Debug.Log(string.Format("[VoxelWorld] teleport want={0} root={1} camLocal={2} camWorld={3} camParent={4}",
                p.ToString("F2"), root.position.ToString("F2"), view.localPosition.ToString("F2"),
                view.position.ToString("F2"), view.parent != null ? view.parent.name : "-"));
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
                    var buildKey = new Vector2Int(bestCell.cellX * 8 + bestEntry.cx,
                                                  bestCell.cellZ * 8 + bestEntry.cz);
                    VxChunk buildChunk;
                    if (!chunkData.TryGetValue(buildKey, out buildChunk))
                        buildChunk = VxCellReader.DecodeChunk(bestCell, bestEntry);
                    CreateChunkObject(buildChunk);
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

                PruneChunkCache(ccx, ccz);
                yield return null;
            }
        }

        /// <summary>
        /// 取相邻 chunk 的体素数据：先查缓存，再从已加载的 cell 里解码并缓存。
        /// 返回 null 表示邻居不存在（地图边缘）或所在 cell 未加载 —— 此时边界按空气处理，仍会出立面。
        /// </summary>
        VxChunk GetNeighborChunk(int cx, int cz)
        {
            var key = new Vector2Int(cx, cz);
            VxChunk cached;
            if (chunkData.TryGetValue(key, out cached)) return cached;

            int cellX = cx >> 3;              // 负数右移即向下取整，与 8×8 cell 划分一致
            int cellZ = cz >> 3;
            VxCell cell;
            if (!cells.TryGetValue(new Vector2Int(cellX, cellZ), out cell)) return null;

            VxCell.ChunkEntry entry;
            if (!cell.lookup.TryGetValue(VxCell.Key(cx - cellX * 8, cz - cellZ * 8), out entry)) return null;

            var chunk = VxCellReader.DecodeChunk(cell, entry);
            chunkData[key] = chunk;           // 缓存，避免每次建网格都重解
            return chunk;
        }

        VxChunk NeighborOf(VxChunk c, int dx, int dz)
        {
            return c == null ? null : GetNeighborChunk(c.chunkX + dx, c.chunkZ + dz);
        }

        /// <summary>邻居缓存只保留 live chunk 附近，超出按距离从远到近丢弃，防止内存随漫游一直涨。</summary>
        void PruneChunkCache(int ccx, int ccz)
        {
            int limit = live.Count + 24;
            if (chunkData.Count <= limit) return;
            pruneScratch.Clear();
            foreach (var kv in chunkData)
                if (!live.ContainsKey(kv.Key)) pruneScratch.Add(kv.Key);
            if (pruneScratch.Count == 0) return;
            pruneScratch.Sort((a, b) =>
            {
                int da = (a.x - ccx) * (a.x - ccx) + (a.y - ccz) * (a.y - ccz);
                int db = (b.x - ccx) * (b.x - ccx) + (b.y - ccz) * (b.y - ccz);
                return db.CompareTo(da);
            });
            for (int i = 0; i < pruneScratch.Count && chunkData.Count > limit; i++)
                chunkData.Remove(pruneScratch[i]);
        }

        bool HasLiveChunkOfCell(Vector2Int cellKey)        {
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
            ChunkMesher.BuildInto(mesh, chunk, palette, band, currentVisibility, verts, normals, colors, tris,
                                  NeighborOf(chunk, -1, 0), NeighborOf(chunk, 1, 0),
                                  NeighborOf(chunk, 0, -1), NeighborOf(chunk, 0, 1));

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = voxelMaterial;
            mr.enabled = mesh.vertexCount > 0;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            live[key] = go;
            chunkData[key] = chunk;
            envelopeDirty = true;
            TotalVerts += mesh.vertexCount;
            TotalTris += tris.Count / 3;
        }

        /// <summary>切换所有已加载 chunk 的共享材质（剖面双面 / 默认单面），不重建网格。</summary>
        public void SetSharedMaterial(Material mat)
        {
            if (mat == null) return;
            voxelMaterial = mat;
            foreach (var kv in live)
            {
                if (kv.Value == null) continue;
                var mr = kv.Value.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = mat;
            }
        }

        // ---------------------------------------------------------- A：岩层包络
        /// <summary>
        /// A 功能每帧调用。普通 chunk 表面在半径内按距离虚化，
        /// 这里生成半径边界处"球外实心块朝向球内"的不透明包络面，负责补背景。
        /// C 正在重建时先隐藏包络，等可见集统一后再重建，避免两套筛选混用。
        /// </summary>
        public void SetEnvelope(bool on, Vector3 center, float radius)
        {
            if (!on)
            {
                if (envelopeMr != null) envelopeMr.enabled = false;
                envelopeWasOn = false;
                return;
            }
            if (palette == null) return;
            if (FilterRebuilding)
            {
                if (envelopeMr != null) envelopeMr.enabled = false;
                envelopeDirty = true;
                envelopeWasOn = false;
                return;
            }
            EnsureEnvelopeObject();
            if (envelopeMaterial == null) return;

            // 包络只做球边界的一层，重建量 O(r^2)；每跨一个方块重建，保证和虚化边界贴合。
            int step = 1;
            Vector3Int b = new Vector3Int(
                Mathf.FloorToInt(center.x),
                Mathf.FloorToInt(center.y),
                Mathf.FloorToInt(center.z));

            bool moved = Mathf.Abs(b.x - envelopeLastBlock.x) >= step ||
                         Mathf.Abs(b.y - envelopeLastBlock.y) >= step ||
                         Mathf.Abs(b.z - envelopeLastBlock.z) >= step;
            bool radiusChanged = Mathf.Abs(radius - envelopeLastRadius) > 0.01f;
            if (!envelopeDirty && envelopeWasOn && !moved && !radiusChanged)
            {
                if (envelopeMr != null) envelopeMr.enabled = envelopeMesh != null && envelopeMesh.vertexCount > 0;
                return;
            }

            envelopeDirty = false;
            envelopeWasOn = true;
            envelopeLastRadius = radius;
            envelopeLastBlock = b;
            RebuildEnvelope(center, radius);
        }

        void EnsureEnvelopeObject()
        {
            if (envelopeGo != null) return;
            if (envelopeMaterial == null) ResolveEnvelopeMaterial();
            if (envelopeMaterial == null) return;

            envelopeGo = new GameObject("VoxelEnvelope");
            envelopeGo.transform.SetParent(transform, false);
            var mf = envelopeGo.AddComponent<MeshFilter>();
            envelopeMesh = new Mesh { name = "VoxelEnvelope" };
            mf.sharedMesh = envelopeMesh;
            envelopeMr = envelopeGo.AddComponent<MeshRenderer>();
            envelopeMr.sharedMaterial = envelopeMaterial;
            envelopeMr.enabled = false;
            envelopeMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            envelopeMr.receiveShadows = false;
            envelopeMr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }

        void ResolveEnvelopeMaterial()
        {
            if (envelopeMaterial != null) return;
            if (voxelMaterial == null) return;
            envelopeMaterial = new Material(voxelMaterial) { name = "VoxelEnvelope_Runtime" };
            envelopeMaterial.SetFloat("_CullMode", 0f);
            envelopeMaterial.SetFloat("_NoFade", 1f);
        }

        void RebuildEnvelope(Vector3 center, float radius)
        {
            if (envelopeMesh == null || envelopeMaterial == null) return;

            int minX = Mathf.FloorToInt(center.x - radius - 1f);
            int maxX = Mathf.CeilToInt(center.x + radius + 1f);
            int minY = Mathf.FloorToInt(center.y - radius - 1f);
            int maxY = Mathf.CeilToInt(center.y + radius + 1f);
            int minZ = Mathf.FloorToInt(center.z - radius - 1f);
            int maxZ = Mathf.CeilToInt(center.z + radius + 1f);
            int sx = maxX - minX + 1;
            int sy = maxY - minY + 1;
            int sz = maxZ - minZ + 1;
            int count = sx * sy * sz;

            if (envelopeVolume == null || envelopeVolume.Length < count)
                envelopeVolume = new ushort[count];
            System.Array.Clear(envelopeVolume, 0, count);

            // 从已解码的 live chunk 直接拷贝包围盒，避免每个体素查字典。
            foreach (var kv in chunkData)
            {
                var c = kv.Value;
                int cx0 = c.chunkX * 16;
                int cz0 = c.chunkZ * 16;
                int x0 = Mathf.Max(minX, cx0);
                int x1 = Mathf.Min(maxX, cx0 + 15);
                int z0 = Mathf.Max(minZ, cz0);
                int z1 = Mathf.Min(maxZ, cz0 + 15);
                int y0 = Mathf.Max(minY, c.BaseY);
                int y1 = Mathf.Min(maxY, c.BaseY + c.sectionCount * 16 - 1);
                if (x0 > x1 || y0 > y1 || z0 > z1) continue;

                for (int y = y0; y <= y1; y++)
                {
                    int syi = y - c.BaseY;
                    for (int z = z0; z <= z1; z++)
                    {
                        int lz = z - cz0;
                        int dstRow = ((y - minY) * sz + (z - minZ)) * sx;
                        int srcRow = syi * 256 + lz * 16;
                        for (int x = x0; x <= x1; x++)
                            envelopeVolume[dstRow + (x - minX)] = c.ids[srcRow + (x - cx0)];
                    }
                }
            }

            ChunkMesher.BuildEnvelopeInto(envelopeMesh, envelopeVolume, sx, sy, sz,
                new Vector3Int(minX, minY, minZ), center, radius, palette, currentVisibility,
                verts, normals, colors, tris);

            envelopeMr.enabled = envelopeMesh.vertexCount > 0;
        }

        void DestroyChunk(Vector2Int key)
        {
            GameObject go;
            if (!live.TryGetValue(key, out go)) return;
            live.Remove(key);
            chunkData.Remove(key);
            envelopeDirty = true;
            if (go != null)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    TotalVerts -= mf.sharedMesh.vertexCount;
                    TotalTris -= (int)mf.sharedMesh.GetIndexCount(0) / 3;
                    Destroy(mf.sharedMesh);
                }
                Destroy(go);
            }
        }

        /// <summary>
        /// 按可见集合重建当前已加载 chunk 的网格。visible == null 表示全部 solid 可见。
        /// 每帧重建 2 个 chunk，避免一次性 0.5s+ 的卡顿；重建期间新加载的 chunk 直接使用新可见集。
        /// </summary>
        public void RebuildVisibility(bool[] visible)
        {
            if (rebuildRoutine != null) StopCoroutine(rebuildRoutine);
            rebuildRoutine = null;
            FilterRebuilding = false;
            currentVisibility = visible;
            envelopeDirty = true;
            if (palette == null) return;
            rebuildRoutine = StartCoroutine(RebuildVisibilityRoutine(visible));
        }

        IEnumerator RebuildVisibilityRoutine(bool[] visible)
        {
            FilterRebuilding = true;
            FilterRebuildDone = 0;
            var keys = new List<Vector2Int>(live.Keys);
            FilterRebuildTotal = keys.Count;
            FilterStatus = "重建网格 0/" + FilterRebuildTotal;
            Debug.Log("[VoxelWorld] filter rebuild start chunks=" + FilterRebuildTotal +
                      " visibility=" + (visible == null ? "all" : "masked"));

            Vector3 p = ViewPos();
            int ccx = Mathf.FloorToInt(p.x / 16f);
            int ccz = Mathf.FloorToInt(p.z / 16f);
            keys.Sort((a, b) =>
            {
                int da = (a.x - ccx) * (a.x - ccx) + (a.y - ccz) * (a.y - ccz);
                int db = (b.x - ccx) * (b.x - ccx) + (b.y - ccz) * (b.y - ccz);
                return da.CompareTo(db);
            });

            int perFrame = 2;
            for (int i = 0; i < keys.Count; i++)
            {
                RebuildChunk(keys[i], visible);
                FilterRebuildDone = i + 1;
                FilterStatus = "重建网格 " + FilterRebuildDone + "/" + FilterRebuildTotal;
                if ((i + 1) % perFrame == 0) yield return null;
            }

            FilterRebuilding = false;
            rebuildRoutine = null;
            FilterStatus = visible == null ? "全部方块可见" : "筛选网格已更新";
            Debug.Log("[VoxelWorld] filter rebuild done chunks=" + FilterRebuildDone +
                      " verts=" + TotalVerts + " tris=" + TotalTris);
        }

        void RebuildChunk(Vector2Int key, bool[] visible)
        {
            GameObject go;
            VxChunk chunk;
            if (!live.TryGetValue(key, out go) || go == null) return;
            if (!chunkData.TryGetValue(key, out chunk)) return;

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;

            var mesh = new Mesh { name = go.name };
            ChunkMesher.BuildInto(mesh, chunk, palette, band, visible, verts, normals, colors, tris,
                                  NeighborOf(chunk, -1, 0), NeighborOf(chunk, 1, 0),
                                  NeighborOf(chunk, 0, -1), NeighborOf(chunk, 0, 1));

            var old = mf.sharedMesh;
            if (old != null)
            {
                TotalVerts -= old.vertexCount;
                TotalTris -= (int)old.GetIndexCount(0) / 3;
            }
            mf.sharedMesh = mesh;
            mr.enabled = mesh.vertexCount > 0;
            TotalVerts += mesh.vertexCount;
            TotalTris += tris.Count / 3;
            if (old != null) Destroy(old);
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

        void LateUpdate()
        {
            if (anchorFrames <= 0 || view == null) return;
            anchorFrames--;
            view.root.position = anchorPos;
            view.localPosition = Vector3.zero;
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
                TotalTris, band > 0 ? band.ToString() : "全高", streamRadius,
                p.x, p.y, p.z, 1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f),
                status);
            GUI.Label(new Rect(16, 120, 1100, 220), text, style);

            if (display != null) display.DrawHud();
        }
    }
}
