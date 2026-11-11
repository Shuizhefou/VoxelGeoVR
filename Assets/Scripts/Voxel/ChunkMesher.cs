using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoxelGeoVR
{
    /// <summary>体素 -> 网格：只生成"可见方块集合"的边界面（隐藏面剔除）。</summary>
    public static class ChunkMesher
    {
        // 与 VxChunk.Id 的邻居查询配合：越界当作空气，chunk 边界出双面（被埋在地下，看不见）
        static readonly Vector3Int[] Dirs =
        {
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
        };

        static readonly Vector3[][] Quads =
        {
            // +Y
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            // -Y
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
            // +X
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            // -X
            new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0) },
            // +Z
            new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1) },
            // -Z
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) }
        };

        static readonly float[] Shades = { 1.00f, 0.50f, 0.80f, 0.80f, 0.66f, 0.66f };

        /// <summary>
        /// 生成"可见集合"的封闭边界：
        /// 可见方块与不可见邻居（空气 / 被筛掉 / 未选中）之间的面全部生成，
        /// 可见方块之间的面剔除。这样只显示铁矿时，每个矿脉都是完整封闭的浮空岛。
        /// visible == null 表示所有 solid 方块都可见。
        /// </summary>
        public static void BuildInto(Mesh mesh, VxChunk c, VoxelPalette pal, int band, bool[] visible,
                                     List<Vector3> verts, List<Vector3> normals,
                                     List<Color32> colors, List<int> tris)
        {
            verts.Clear();
            normals.Clear();
            colors.Clear();
            tris.Clear();

            // 按 chunk 取 y 窗口（而不是按列裁剪）：按列裁剪会把树冠下面的地面切掉，
            // 树/悬崖会变成"悬空薄片"。窗口 = [min(top)-band+1, max(top)]。
            // band <= 0 表示不裁剪：整个 chunk 从最底层 section 到最高点都出网格。
            int baseY = c.BaseY;
            int ymin = int.MaxValue, ymax = int.MinValue;
            for (int i = 0; i < 256; i++)
            {
                short t = c.top[i];
                if (t == short.MinValue) continue;
                if (t < ymin) ymin = t;
                if (t > ymax) ymax = t;
            }
            if (ymin == int.MaxValue) return;
            ymin = band > 0 ? Mathf.Max(ymin - band + 1, baseY) : baseY;
            ymax = Mathf.Min(ymax, baseY + c.sectionCount * 16 - 1);

            for (int y = ymin; y <= ymax; y++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        ushort id = c.Id(x, y, z);
                        if (!pal.IsSolid(id)) continue;
                        if (visible != null && (id >= visible.Length || !visible[id])) continue;
                        Color32 col = pal.Color(id);

                        for (int f = 0; f < 6; f++)
                        {
                            var dir = Dirs[f];
                            ushort nid = c.Id(x + dir.x, y + dir.y, z + dir.z);
                            bool neighborVisible = pal.IsSolid(nid) &&
                                (visible == null || (nid < visible.Length && visible[nid]));
                            if (neighborVisible) continue;

                            var q = Quads[f];
                            int vi = verts.Count;
                            float shade = Shades[f];
                            Color32 shaded = new Color32(
                                (byte)Mathf.Clamp(col.r * shade, 0f, 255f),
                                (byte)Mathf.Clamp(col.g * shade, 0f, 255f),
                                (byte)Mathf.Clamp(col.b * shade, 0f, 255f),
                                255);
                            Vector3 n = new Vector3(dir.x, dir.y, dir.z);
                            for (int k = 0; k < 4; k++)
                            {
                                verts.Add(new Vector3(x + q[k].x, y + q[k].y, z + q[k].z));
                                normals.Add(n);
                                colors.Add(shaded);
                            }
                            tris.Add(vi); tris.Add(vi + 1); tris.Add(vi + 2);
                            tris.Add(vi); tris.Add(vi + 2); tris.Add(vi + 3);
                        }
                    }
                }
            }

            mesh.Clear();
            if (verts.Count == 0) return;
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// A 功能的"岩层包络面"：只生成球边界处、球外实心块朝向球内的那层不透明壳。
        /// 普通 chunk 表面仍在半径内按距离虚化；虚化露出的背景由这层包络补上。
        /// 条件：球外 solid（visible）且邻居在球内且也是 solid（visible）。
        /// 球内为空气/被 C 隐藏时，普通 chunk mesh 本身已有对应空气面，不在这里重复生成。
        /// volume 是轴对齐包围盒内的 blockId，index = (y * sz + z) * sx + x。
        /// </summary>
        public static void BuildEnvelopeInto(Mesh mesh, ushort[] volume, int sx, int sy, int sz,
                                             Vector3Int origin, Vector3 center, float radius,
                                             VoxelPalette pal, bool[] visible,
                                             List<Vector3> verts, List<Vector3> normals,
                                             List<Color32> colors, List<int> tris)
        {
            verts.Clear();
            normals.Clear();
            colors.Clear();
            tris.Clear();

            float r2 = radius * radius;
            for (int y = 0; y < sy; y++)
            {
                for (int z = 0; z < sz; z++)
                {
                    for (int x = 0; x < sx; x++)
                    {
                        int idx = (y * sz + z) * sx + x;
                        ushort id = volume[idx];
                        if (!pal.IsSolid(id)) continue;
                        if (visible != null && (id >= visible.Length || !visible[id])) continue;

                        float wx = origin.x + x + 0.5f;
                        float wy = origin.y + y + 0.5f;
                        float wz = origin.z + z + 0.5f;
                        float dx = wx - center.x, dy = wy - center.y, dz = wz - center.z;
                        if (dx * dx + dy * dy + dz * dz <= r2) continue;   // 只处理球外块

                        Color32 col = pal.Color(id);
                        for (int f = 0; f < 6; f++)
                        {
                            var dir = Dirs[f];
                            int nx = x + dir.x, ny = y + dir.y, nz = z + dir.z;
                            if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;

                            float nwx = origin.x + nx + 0.5f;
                            float nwy = origin.y + ny + 0.5f;
                            float nwz = origin.z + nz + 0.5f;
                            float ndx = nwx - center.x, ndy = nwy - center.y, ndz = nwz - center.z;
                            if (ndx * ndx + ndy * ndy + ndz * ndz > r2) continue; // 邻居必须在球内

                            ushort nid = volume[(ny * sz + nz) * sx + nx];
                            bool nvis = pal.IsSolid(nid) &&
                                (visible == null || (nid < visible.Length && visible[nid]));
                            if (!nvis) continue; // 球内空气/被筛掉：由普通表面负责

                            var q = Quads[f];
                            int vi = verts.Count;
                            float shade = Shades[f];
                            Color32 shaded = new Color32(
                                (byte)Mathf.Clamp(col.r * shade, 0f, 255f),
                                (byte)Mathf.Clamp(col.g * shade, 0f, 255f),
                                (byte)Mathf.Clamp(col.b * shade, 0f, 255f),
                                255);
                            Vector3 n = new Vector3(dir.x, dir.y, dir.z);
                            for (int k = 0; k < 4; k++)
                            {
                                verts.Add(new Vector3(
                                    origin.x + x + q[k].x,
                                    origin.y + y + q[k].y,
                                    origin.z + z + q[k].z));
                                normals.Add(n);
                                colors.Add(shaded);
                            }
                            tris.Add(vi); tris.Add(vi + 1); tris.Add(vi + 2);
                            tris.Add(vi); tris.Add(vi + 2); tris.Add(vi + 3);
                        }
                    }
                }
            }

            mesh.Clear();
            if (verts.Count == 0) return;
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
        }
    }
}
