using System;
using UnityEngine;

namespace Domain.UI
{
    /// <summary>
    /// UI 资源加载抽象——UIManager 与资源系统之间的接缝。
    /// 回调式签名是有意的：Resources 同步加载当帧回调，Addressables（M3）异步完成后回调，
    /// UIManager 两种情况下代码不变。
    /// </summary>
    public interface IUIAssetLoader
    {
        /// <summary>按 key 加载界面 prefab。失败回调 null（由调用方报错）。</summary>
        void Load(string key, Action<GameObject> onLoaded);

        /// <summary>界面关闭后释放。Resources 下是空操作；Addressables 下归还 handle。</summary>
        void Release(string key);
    }

    /// <summary>Resources 实现：key = Resources 相对路径（如 "UI/BattleHud"）。</summary>
    public sealed class ResourcesUIAssetLoader : IUIAssetLoader
    {
        public void Load(string key, Action<GameObject> onLoaded)
            => onLoaded(Resources.Load<GameObject>(key));

        public void Release(string key)
        {
            // Resources 由 Unity 统一管理，无逐资源释放语义
        }
    }
}
