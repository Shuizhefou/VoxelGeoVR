using System;
using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>blocks.json -> 颜色/实心查表。</summary>
    public class VoxelPalette
    {
        public readonly Color32[] colors;
        public readonly bool[] solid;
        public readonly bool[] translucent;
        public readonly string[] names;
        public readonly int count;

        static readonly Color32 Fallback = new Color32(128, 128, 128, 255);

        public VoxelPalette(VxBlocksDoc doc)
        {
            var list = doc != null ? doc.blocks : null;
            count = list != null ? list.Length : 0;
            int maxId = 0;
            if (list != null)
                for (int i = 0; i < list.Length; i++) maxId = Mathf.Max(maxId, list[i].id);
            colors = new Color32[maxId + 1];
            solid = new bool[maxId + 1];
            translucent = new bool[maxId + 1];
            names = new string[maxId + 1];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = Fallback;
                solid[i] = true;
            }
            if (list == null) return;
            for (int i = 0; i < list.Length; i++)
            {
                var b = list[i];
                if (b.id < 0 || b.id >= colors.Length) continue;
                // 工程是 Linear 色彩空间：顶点色按数值直传，所以这里先把 sRGB 转成线性，
                // 否则整体会发白（实测比原方块浅一档）。
                colors[b.id] = ToLinear(ParseHex(b.color));
                solid[b.id] = b.solid;
                translucent[b.id] = b.translucent;
                names[b.id] = string.IsNullOrEmpty(b.zh) ? b.name : b.zh;
            }
            if (names.Length > 0)
            {
                names[0] = "空气";
                solid[0] = false;
            }
        }

        public static Color32 ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Fallback;
            hex = hex.TrimStart('#');
            if (hex.Length < 6) return Fallback;
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            int a = hex.Length >= 8 ? Convert.ToInt32(hex.Substring(6, 2), 16) : 255;
            return new Color32((byte)r, (byte)g, (byte)b, (byte)a);
        }

        public Color32 Color(int id)
        {
            return (uint)id < (uint)colors.Length ? colors[id] : Fallback;
        }

        static Color32 ToLinear(Color32 c)
        {
            Color lin = ((Color)c).linear;
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(lin.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(lin.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(lin.b * 255f), 0, 255),
                255);
        }

        public bool IsSolid(int id)
        {
            return id != 0 && (uint)id < (uint)solid.Length && solid[id];
        }
    }
}
