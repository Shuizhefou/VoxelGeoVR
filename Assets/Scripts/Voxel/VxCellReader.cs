using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>一个 chunk 的体素数据（按 section 连续存放的 ushort blockId）。</summary>
    public class VxChunk
    {
        public int chunkX, chunkZ;
        public int minSectionY;      // section 起点（世界 y = minSectionY*16）
        public int sectionCount;
        public short[] top;          // 每列最高实心 y，short.MinValue = 空列
        public ushort[] ids;         // sectionCount * 4096
        public int solidCount;

        public int BaseY { get { return minSectionY * 16; } }

        public int Index(int x, int yAbs, int z)
        {
            int sy = yAbs - BaseY;
            return (sy * 16 + z) * 16 + x;
        }

        public ushort Id(int x, int yAbs, int z)
        {
            if (x < 0 || x > 15 || z < 0 || z > 15) return 0;
            int sy = yAbs - BaseY;
            if (sy < 0 || sy >= sectionCount * 16) return 0;
            return ids[(sy * 256) + z * 16 + x];
        }
    }

    /// <summary>一个 cell（8×8 chunk）文件。</summary>
    public class VxCell
    {
        public int cellX, cellZ;
        public int count;
        public ChunkEntry[] entries;
        public byte[] data;
        public readonly Dictionary<long, ChunkEntry> lookup = new Dictionary<long, ChunkEntry>();

        public struct ChunkEntry
        {
            public int cx, cz;
            public int sectionCount;
            public int offset, length, solid;
        }

        public static long Key(int cx, int cz)
        {
            return ((long)cx << 32) ^ (uint)cz;
        }
    }

    public static class VxCellReader
    {
        static readonly byte[] Magic = { (byte)'V', (byte)'X', (byte)'C', (byte)'1' };

        public static VxCell ParseCell(byte[] blob)
        {
            if (blob == null || blob.Length < 32) throw new Exception("cell too small");
            using (var ms = new MemoryStream(blob, false))
            using (var br = new BinaryReader(ms))
            {
                var magic = br.ReadBytes(4);
                for (int i = 0; i < 4; i++)
                    if (magic[i] != Magic[i]) throw new Exception("bad cell magic");
                int version = br.ReadUInt16();
                int headerBytes = br.ReadUInt16();
                int cellX = br.ReadInt32();
                int cellZ = br.ReadInt32();
                int count = br.ReadInt32();
                br.ReadInt32();                 // palette count
                uint flags = br.ReadUInt32();
                int payloadBytes = br.ReadInt32();

                if (version != 1) throw new Exception("unsupported cell version " + version);
                ms.Position = headerBytes;
                byte[] payload = br.ReadBytes(payloadBytes);
                byte[] raw = (flags & 1) != 0 ? Inflate(payload) : payload;

                var cell = new VxCell { cellX = cellX, cellZ = cellZ, count = count, entries = new VxCell.ChunkEntry[count] };
                using (var ms2 = new MemoryStream(raw, false))
                using (var br2 = new BinaryReader(ms2))
                {
                    int indexBytes = count * 16;
                    for (int i = 0; i < count; i++)
                    {
                        var e = new VxCell.ChunkEntry
                        {
                            cx = br2.ReadSByte(),
                            cz = br2.ReadSByte()
                        };
                        br2.ReadByte();             // flags
                        e.sectionCount = br2.ReadByte();
                        e.offset = br2.ReadInt32();
                        e.length = br2.ReadInt32();
                        e.solid = br2.ReadInt32();
                        cell.entries[i] = e;
                        cell.lookup[VxCell.Key(e.cx, e.cz)] = e;
                    }
                    cell.data = new byte[raw.Length - indexBytes];
                    Buffer.BlockCopy(raw, indexBytes, cell.data, 0, cell.data.Length);
                }
                return cell;
            }
        }

        public static VxChunk DecodeChunk(VxCell cell, VxCell.ChunkEntry e)
        {
            var chunk = new VxChunk
            {
                chunkX = cell.cellX * 8 + e.cx,
                chunkZ = cell.cellZ * 8 + e.cz,
                solidCount = e.solid
            };
            int baseOffset = e.offset;
            int p = baseOffset;
            byte[] d = cell.data;
            chunk.minSectionY = (short)(d[p] | (d[p + 1] << 8)); p += 2;
            chunk.sectionCount = d[p]; p += 1;
            chunk.top = new short[256];
            for (int i = 0; i < 256; i++)
            {
                chunk.top[i] = (short)(d[p] | (d[p + 1] << 8));
                p += 2;
            }
            chunk.ids = new ushort[chunk.sectionCount * 4096];
            for (int s = 0; s < chunk.sectionCount; s++)
            {
                byte mode = d[p++];
                int voxBase = s * 4096;
                if (mode == 0)
                {
                    continue;
                }
                if (mode == 1)
                {
                    ushort id = (ushort)(d[p] | (d[p + 1] << 8)); p += 2;
                    for (int i = 0; i < 4096; i++) chunk.ids[voxBase + i] = id;
                    continue;
                }
                if (mode != 2) throw new Exception("bad section mode " + mode);
                int bits = d[p++];
                int palN = d[p] | (d[p + 1] << 8); p += 2;
                var pal = new ushort[palN];
                for (int i = 0; i < palN; i++)
                {
                    pal[i] = (ushort)(d[p] | (d[p + 1] << 8));
                    p += 2;
                }
                int per = 64 / bits;
                int words = (4096 + per - 1) / per;
                ulong mask = (ulong)((1L << bits) - 1L);
                for (int w = 0; w < words; w++)
                {
                    ulong word = 0;
                    int wb = p + w * 8;
                    for (int b = 0; b < 8; b++) word |= (ulong)d[wb + b] << (8 * b);
                    int baseIdx = w * per;
                    for (int k = 0; k < per; k++)
                    {
                        if (baseIdx + k >= 4096) break;
                        int idx = (int)((word >> (k * bits)) & mask);
                        chunk.ids[voxBase + baseIdx + k] = (idx < palN && idx >= 0) ? pal[idx] : (ushort)0;
                    }
                }
                p += words * 8;
            }
            return chunk;
        }

        public static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(data.Length * 6))
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
