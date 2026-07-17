using System;
using System.IO;
using Domain.Infrastructure.Replay;
using UnityEngine;

namespace Domain.Service.Replay
{
    /// <summary>
    /// 回放文件存取（Runtime 层：Core 的 ReplayIO 只管格式，这里管"存哪儿"）。
    /// 目录：persistentDataPath/Replays/，文件名带本地时间戳，字典序即时间序。
    /// </summary>
    public static class ReplayFileStore
    {
        private const string Extension = ".ftgr";

        private static string Directory
            => Path.Combine(Application.persistentDataPath, "Replays");

        /// <summary>存一场回放，返回完整路径。</summary>
        public static string Save(ReplayData data)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = Path.Combine(Directory,
                $"replay_{DateTime.Now:yyyyMMdd_HHmmss}{Extension}");
            using (FileStream stream = File.Create(path))
                ReplayIO.Save(data, stream);
            Debug.Log($"[Replay] 已保存：{path}（{data.FrameCount} 帧）");
            return path;
        }

        /// <summary>读最近一场回放。没有或损坏返回 null（并给出日志）。</summary>
        public static ReplayData LoadLatest()
        {
            if (!System.IO.Directory.Exists(Directory)) return null;

            string[] files = System.IO.Directory.GetFiles(Directory, "*" + Extension);
            if (files.Length == 0) return null;

            Array.Sort(files, StringComparer.Ordinal); // 时间戳命名 → 字典序 = 时间序
            string latest = files[files.Length - 1];
            try
            {
                using (FileStream stream = File.OpenRead(latest))
                    return ReplayIO.Load(stream);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Replay] 读取失败 {latest}：{e.Message}");
                return null;
            }
        }
    }
}
