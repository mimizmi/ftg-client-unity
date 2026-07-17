using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Domain.UI
{
    /// <summary>
    /// Addressables 实现：key = 资源 address（沿用原 Resources 相对路径命名，如 "UI/BattleHud"，
    /// 调用方零改动，这正是 IUIAssetLoader 接缝的意义）。加载是真异步——完成后回调；
    /// 同 key 引用计数，最后一次 Release 才归还 handle。
    /// address 背后是本地组还是远端热更下来的 bundle，这里不关心——热更只动 catalog，不动这段代码。
    /// </summary>
    public sealed class AddressablesUIAssetLoader : IUIAssetLoader
    {
        private sealed class Entry
        {
            public AsyncOperationHandle<GameObject> Handle;
            public int RefCount;
        }

        private readonly Dictionary<string, Entry> loaded = new Dictionary<string, Entry>();

        public void Load(string key, Action<GameObject> onLoaded)
        {
            if (loaded.TryGetValue(key, out Entry entry))
            {
                entry.RefCount++;
                if (entry.Handle.IsDone) onLoaded(ResultOf(entry.Handle, key));
                else entry.Handle.Completed += h => onLoaded(ResultOf(h, key));
                return;
            }

            entry = new Entry { Handle = Addressables.LoadAssetAsync<GameObject>(key), RefCount = 1 };
            loaded[key] = entry;
            entry.Handle.Completed += h =>
            {
                GameObject prefab = ResultOf(h, key);
                if (prefab == null)
                {
                    // 加载失败：不会有界面实例诞生，也就等不来对应的 Release——当场清账
                    loaded.Remove(key);
                    Addressables.Release(h);
                }
                onLoaded(prefab);
            };
        }

        public void Release(string key)
        {
            if (!loaded.TryGetValue(key, out Entry entry)) return;
            if (--entry.RefCount > 0) return;
            loaded.Remove(key);
            Addressables.Release(entry.Handle);
        }

        private static GameObject ResultOf(AsyncOperationHandle<GameObject> handle, string key)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded) return handle.Result;
            Debug.LogError($"[AddressablesUIAssetLoader] 加载失败：\"{key}\"" +
                           "（address 是纯字符串且区分大小写，Resources 在 Windows 上不区分——检查是否精确匹配）");
            return null;
        }
    }
}
