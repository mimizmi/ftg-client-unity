using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Domain.Service.App
{
    /// <summary>
    /// 按 address 同步读取 TextAsset 文本，取不到返回 null。
    /// 注入给 ExampleFighterDefinitionRepository / BoxDataLoader 当文本来源——
    /// 帧数据 JSON 从此走 Addressables，remote catalog 一更新，改帧数据不用发版。
    /// WaitForCompletion 的前提：启动时 HotUpdater 已把 preload 标签内容拉到本地，
    /// 这里只是从本地缓存同步取，不会卡在网络上。
    /// </summary>
    public static class AddressablesTextReader
    {
        public static string Read(string address)
        {
            AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(address);
            try
            {
                TextAsset asset = handle.WaitForCompletion();
                return asset != null ? asset.text : null; // 返回值先于 finally 求值，text 已安全拷出
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AddressablesTextReader] 读取 \"{address}\" 失败：{e.Message}");
                return null;
            }
            finally
            {
                Addressables.Release(handle);
            }
        }
    }
}
