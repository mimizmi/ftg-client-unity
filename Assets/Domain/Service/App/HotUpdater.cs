using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Domain.Service.App
{
    /// <summary>
    /// 启动热更器：检查 remote catalog → 更新目录 → 计算增量 → 预下载。
    /// 机制：构建时开启 Build Remote Catalog；运行时对比 catalog hash，有变化就拉新目录 +
    /// 增量 bundle——代码零改动，数据（帧数据 JSON / 角色包 / UI）即时生效。
    /// 【离线也要能玩】：任何一步失败只警告并继续，用本地缓存的版本进游戏。
    /// 由 GameFlowController 在进主菜单前调用，进度经 onStatus 打到 Loading 界面。
    /// </summary>
    public sealed class HotUpdater : MonoBehaviour
    {
        [Tooltip("启动时预下载的 Addressables 标签（给需要随更即得的组里的条目打上）")]
        [SerializeField] private string[] preloadLabels = { "preload" };

        public void Run(Action<string> onStatus, Action onFinished)
            => StartCoroutine(RunRoutine(onStatus, onFinished));

        private IEnumerator RunRoutine(Action<string> onStatus, Action onFinished)
        {
            onStatus?.Invoke("初始化资源系统");
            yield return Addressables.InitializeAsync(); // 句柄默认自动释放

            onStatus?.Invoke("检查资源更新");
            AsyncOperationHandle<List<string>> check = Addressables.CheckForCatalogUpdates(false);
            yield return check;
            List<string> stale = check.Status == AsyncOperationStatus.Succeeded ? check.Result : null;
            Addressables.Release(check);

            if (stale != null && stale.Count > 0)
            {
                onStatus?.Invoke("更新资源目录");
                AsyncOperationHandle<List<IResourceLocator>> update = Addressables.UpdateCatalogs(stale, false);
                yield return update;
                if (update.Status != AsyncOperationStatus.Succeeded)
                    Debug.LogWarning("[HotUpdater] catalog 更新失败，继续使用本地版本");
                Addressables.Release(update);
            }

            if (preloadLabels != null && preloadLabels.Length > 0)
            {
                AsyncOperationHandle<long> size = Addressables.GetDownloadSizeAsync((IEnumerable)preloadLabels);
                yield return size;
                long bytes = size.Status == AsyncOperationStatus.Succeeded ? size.Result : 0L;
                Addressables.Release(size);

                if (bytes > 0)
                {
                    AsyncOperationHandle download = Addressables.DownloadDependenciesAsync(
                        (IEnumerable)preloadLabels, Addressables.MergeMode.Union, false);
                    while (!download.IsDone)
                    {
                        DownloadStatus st = download.GetDownloadStatus();
                        onStatus?.Invoke(
                            $"下载资源 {st.DownloadedBytes / 1048576f:F1}/{st.TotalBytes / 1048576f:F1} MB");
                        yield return null;
                    }
                    if (download.Status != AsyncOperationStatus.Succeeded)
                        Debug.LogWarning("[HotUpdater] 资源预下载失败，进入游戏后按需加载");
                    Addressables.Release(download);
                }
            }

            onFinished?.Invoke();
        }
    }
}
