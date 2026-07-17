using Loxodon.Framework.Binding;
using Loxodon.Framework.Binding.Builder;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI.Flow
{
    /// <summary>结算视图。Layer 应设为 Window（盖在 HUD 上）。</summary>
    public sealed class ResultView : UIScreen
    {
        [Header("留空 = 按约定路径自动查找")]
        [SerializeField] private TMP_Text winnerText;
        [SerializeField] private Button rematchButton;
        [SerializeField] private Button menuButton;

        private void Awake()
        {
            winnerText = winnerText != null ? winnerText : Find<TMP_Text>("Winner");
            rematchButton = rematchButton != null ? rematchButton : Find<Button>("Rematch");
            menuButton = menuButton != null ? menuButton : Find<Button>("Menu");
        }

        public void Bind(ResultViewModel vm)
        {
            BindingSet<ResultView, ResultViewModel> set =
                this.CreateBindingSet<ResultView, ResultViewModel>(vm);
            set.Bind(winnerText).For(v => v.text).To(x => x.WinnerText).OneWay();
            set.Bind(rematchButton).For(v => v.onClick).To(x => x.RematchCommand);
            set.Bind(menuButton).For(v => v.onClick).To(x => x.MenuCommand);
            set.Build();
        }

        private T Find<T>(string path) where T : Component
        {
            Transform child = transform.Find(path);
            if (child == null)
            {
                Debug.LogError($"[ResultView] 缺少子节点 \"{path}\"（或在 Inspector 拖引用）", this);
                return null;
            }
            var component = child.GetComponent<T>();
            if (component == null)
                Debug.LogError($"[ResultView] \"{path}\" 上缺少 {typeof(T).Name} 组件", child);
            return component;
        }
    }
}
