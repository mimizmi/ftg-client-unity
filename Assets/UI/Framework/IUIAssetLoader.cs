using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Domain.UI
{
    /// <summary>
    /// UI 资源加载抽象——UIManager 与资源系统之间的接缝。
    /// UniTask 签名（M6 起）：Resources 返回已完成的 task（同步语义不变），
    /// Addressables 真异步——UIManager 两种情况下同一句 await，接缝依旧成立。
    /// </summary>
    public interface IUIAssetLoader
    {
        /// <summary>按 key 加载界面 prefab。失败返回 null（由调用方报错）。</summary>
        UniTask<GameObject> Load(string key);

        /// <summary>界面关闭后释放。Resources 下是空操作；Addressables 下归还 handle。</summary>
        void Release(string key);
    }

    /// <summary>Resources 实现：key = Resources 相对路径（如 "UI/BattleHud"）。</summary>
    public sealed class ResourcesUIAssetLoader : IUIAssetLoader
    {
        public UniTask<GameObject> Load(string key)
            => UniTask.FromResult(Resources.Load<GameObject>(key));

        public void Release(string key)
        {
            // Resources 由 Unity 统一管理，无逐资源释放语义
        }
    }
}
