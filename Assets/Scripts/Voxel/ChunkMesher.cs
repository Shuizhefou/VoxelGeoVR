using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoxelGeoVR
{
    /// <summary>体素 -> 网格：只生成"地表以下 band 层"内可见面（隐藏面剔除）。</summary>
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

        public static void BuildInto(Mesh mesh, VxChunk c, VoxelPalette pal, int band,
                                     List<Vector3> verts, List<Vector3> normals,
                                     List<Color32> colors, List<int> tris)
        {
            verts.Clear();
            normals.Clear();
            colors.Clear();
            tris.Clear();

            // 按 chunk 取一个 y 窗口（而不是按列裁剪）：按列裁剪会把树冠下面的地面切掉，
            // 树/悬崖会变成"悬空薄片"。窗口 = [min(top)-band+1, max(top)]。
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
            ymin = Mathf.Max(ymin - band + 1, baseY);
            ymax = Mathf.Min(ymax, baseY + c.sectionCount * 16 - 1);

            for (int y = ymin; y <= ymax; y++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        ushort id = c.Id(x, y, z);
                        if (!pal.IsSolid(id)) continue;
                        Color32 col = pal.Color(id);

                        for (int f = 0; f < 6; f++)
                        {
                            var dir = Dirs[f];
                            if (pal.IsSolid(c.Id(x + dir.x, y + dir.y, z + dir.z))) continue;

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
    }
}
