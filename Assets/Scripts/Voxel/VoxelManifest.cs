using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace VoxelGeoVR
{
    [Serializable]
    public class VxWorldManifest
    {
        public string formatId;
        public string name;
        public VxSource source;
        public VxAxes axes;
        public VxBounds bounds;
        public VxSpawn spawn;
        public int cellSize;
        public int bandDefault;
        public string blocks;
        public float[] viewpoint;
        public float[] spawnView;
        public VxCellInfo[] cells;
    }

    [Serializable]
    public class VxSource
    {
        public string mcVersion;
        public int dataVersion;
        public long seed;
        public string[] regionMd5;
    }

    [Serializable]
    public class VxAxes
    {
        public string up;
        public string mapping;
        public float blockSizeMeters;
    }

    [Serializable]
    public class VxBounds
    {
        public int[] chunkX;
        public int[] chunkZ;
        public int[] blockX;
        public int[] blockZ;
        public int[] blockY;
        public int[] surfaceYRange;
    }

    [Serializable]
    public class VxSpawn
    {
        public int[] block;
        public int[] chunk;
    }

    [Serializable]
    public class VxCellInfo
    {
        public string file;
        public int cellX;
        public int cellZ;
        public int chunkMinX;
        public int chunkMaxX;
        public int chunkMinZ;
        public int chunkMaxZ;
        public int chunks;
        public int bytes;
        public string md5;
    }

    [Serializable]
    public class VxBlocksDoc
    {
        public string formatId;
        public VxBlockInfo[] blocks;
    }

    [Serializable]
    public class VxBlockInfo
    {
        public int id;
        public string name;
        public string zh;
        public string color;
        public bool solid;
        public bool translucent;
    }

    [Serializable]
    public class VxReport
    {
        public VxReportSummary summary;
    }

    [Serializable]
    public class VxReportSummary
    {
        public string world;
        public string outDir;
        public int chunks;
        public int cells;
        public long solidBlocks;
        public long faces;
        public float facesPerChunk;
        public int bytes;
        public float seconds;
    }

    /// <summary>
    /// StreamingAssets 读取：Android 上 StreamingAssets 在 APK(jar) 内，必须用 UnityWebRequest；
    /// 编辑器/桌面用 File 直接读。
    /// </summary>
    public static class VxFiles
    {
        public const string Root = "vxworld";

        public static string PathOf(string rel)
        {
            return Path.Combine(Application.streamingAssetsPath, Root, rel);
        }

        public static bool NeedsWebRequest
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public static IEnumerator ReadText(string rel, Action<string> done, Action<string> fail = null)
        {
            string path = PathOf(rel);
            if (!NeedsWebRequest)
            {
                string text = null;
                string err = null;
                try { text = File.ReadAllText(path); }
                catch (Exception e) { err = e.Message; }
                if (text != null) done?.Invoke(text);
                else fail?.Invoke(err ?? ("cannot read " + path));
                yield break;
            }

            string url = path.Contains("://") ? path : "file://" + path;
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) done?.Invoke(req.downloadHandler.text);
                else fail?.Invoke(req.error + " @ " + url);
            }
        }

        public static IEnumerator ReadBytes(string rel, Action<byte[]> done, Action<string> fail = null)
        {
            string path = PathOf(rel);
            if (!NeedsWebRequest)
            {
                byte[] data = null;
                string err = null;
                try { data = File.ReadAllBytes(path); }
                catch (Exception e) { err = e.Message; }
                if (data != null) done?.Invoke(data);
                else fail?.Invoke(err ?? ("cannot read " + path));
                yield break;
            }

            string url = path.Contains("://") ? path : "file://" + path;
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) done?.Invoke(req.downloadHandler.data);
                else fail?.Invoke(req.error + " @ " + url);
            }
        }

        public static string ReadTextSync(string rel)
        {
            return File.ReadAllText(PathOf(rel));
        }

        public static byte[] ReadBytesSync(string rel)
        {
            return File.ReadAllBytes(PathOf(rel));
        }
    }
}
