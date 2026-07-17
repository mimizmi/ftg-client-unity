using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI
{
    /// <summary>
    /// 层级栈式 UI 管理器。职责：
    /// ① 启动时按 UILayer 建 5 个独立 Canvas（sortingOrder 分层，互不重建）；
    /// ② Open：经 IUIAssetLoader 加载 prefab（同步/异步透明）→ 实例化到其声明的层 → 压栈 → 生命周期回调；
    /// ③ Close：出栈 → OnClosed → 销毁 → 释放资源 → 焦点还给新栈顶。
    /// 界面层级由 prefab 自己声明（UIScreen.layer），调用方只认 key——数据驱动。
    /// 资源来源可替换（Resources ↔ Addressables）而本类零改动，这是 M3 热更的接缝。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class UIManager : MonoBehaviour
    {
        [SerializeField] private Vector2 referenceResolution = new Vector2(1920, 1080);

        [Tooltip("勾选 = UI prefab 经 Addressables 按 address 加载（address 沿用原 Resources 路径，如 \"UI/BattleHud\"）；" +
                 "不勾 = Resources 同步加载。M3 起勾选。")]
        [SerializeField] private bool useAddressables;

        private readonly Dictionary<UILayer, RectTransform> layerRoots =
            new Dictionary<UILayer, RectTransform>();
        private readonly Dictionary<UILayer, List<UIScreen>> layerStacks =
            new Dictionary<UILayer, List<UIScreen>>();

        /// <summary>可替换的资源后端（Awake 按 useAddressables 选定；测试/特殊场景可再覆盖）。</summary>
        public IUIAssetLoader AssetLoader { get; set; } = new ResourcesUIAssetLoader();

        private void Awake()
        {
            if (useAddressables) AssetLoader = new AddressablesUIAssetLoader();

            foreach (UILayer layer in Enum.GetValues(typeof(UILayer)))
            {
                var go = new GameObject($"Layer_{layer}",
                    typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                go.transform.SetParent(transform, false);

                var canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = (int)layer * 100;

                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = referenceResolution;
                scaler.matchWidthOrHeight = 0.5f;

                // Toast 层永不拦截点击
                if (layer == UILayer.Toast)
                    go.GetComponent<GraphicRaycaster>().enabled = false;

                layerRoots[layer] = (RectTransform)go.transform;
                layerStacks[layer] = new List<UIScreen>();
            }
        }

        /// <summary>
        /// 打开界面。层级由 prefab 上的 UIScreen.layer 决定。
        /// onOpened 在界面完成入栈后回调（异步加载时机不确定，不要依赖返回时序）。
        /// </summary>
        public void Open<T>(string key, object arg = null, Action<T> onOpened = null) where T : UIScreen
        {
            AssetLoader.Load(key, prefab =>
            {
                if (prefab == null)
                {
                    Debug.LogError($"[UIManager] 找不到 UI 资源：\"{key}\"（检查 Resources 路径 / Addressables key）");
                    return;
                }

                GameObject instance = Instantiate(prefab);
                var screen = instance.GetComponent<T>();
                if (screen == null)
                {
                    Debug.LogError($"[UIManager] \"{key}\" 的 prefab 根节点上没有 {typeof(T).Name} 组件");
                    Destroy(instance);
                    return;
                }

                instance.transform.SetParent(layerRoots[screen.Layer], false);

                List<UIScreen> stack = layerStacks[screen.Layer];
                if (stack.Count > 0) stack[stack.Count - 1].OnBlur();
                stack.Add(screen);

                screen.Key = key;
                screen.OnOpened(arg);
                screen.OnFocus();
                onOpened?.Invoke(screen);
            });
        }

        /// <summary>关闭界面：出栈 → OnClosed → 销毁 → 释放资源 → 焦点还给新栈顶。</summary>
        public void Close(UIScreen screen)
        {
            if (screen == null) return;

            List<UIScreen> stack = layerStacks[screen.Layer];
            bool wasTop = stack.Count > 0 && stack[stack.Count - 1] == screen;
            if (!stack.Remove(screen))
            {
                Debug.LogWarning($"[UIManager] 试图关闭未受管理的界面：{screen.name}");
                return;
            }

            screen.OnClosed();
            Destroy(screen.gameObject);
            AssetLoader.Release(screen.Key);

            if (wasTop && stack.Count > 0) stack[stack.Count - 1].OnFocus();
        }

        /// <summary>关闭某层当前栈顶（暂停菜单"返回"键的语义）。</summary>
        public void CloseTop(UILayer layer)
        {
            List<UIScreen> stack = layerStacks[layer];
            if (stack.Count > 0) Close(stack[stack.Count - 1]);
        }

        /// <summary>某层当前栈顶界面（无则 null）。</summary>
        public UIScreen Peek(UILayer layer)
        {
            List<UIScreen> stack = layerStacks[layer];
            return stack.Count > 0 ? stack[stack.Count - 1] : null;
        }
    }
}
