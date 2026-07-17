using Loxodon.Framework.Binding;
using Loxodon.Framework.Binding.Builder;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 公告窗视图（Window 层）：纯壳——显示什么、点关闭后发生什么，全由 VM
    /// （背后是 Lua 热更脚本）决定。控件留空则按名字深度查找：Title / Body / Close。
    /// </summary>
    public sealed class NoticeView : UIScreen
    {
        [Header("留空 = 按名字自动查找（Title / Body / Close）")]
        [SerializeField] private TMP_Text title;
        [SerializeField] private TMP_Text body;
        [SerializeField] private Button closeButton;

        public void Bind(NoticeViewModel vm)
        {
            if (title == null) title = FindDeep<TMP_Text>("Title");
            if (body == null) body = FindDeep<TMP_Text>("Body");
            if (closeButton == null) closeButton = FindDeep<Button>("Close");

            BindingSet<NoticeView, NoticeViewModel> set =
                this.CreateBindingSet<NoticeView, NoticeViewModel>(vm);
            set.Bind(title).For(v => v.text).To(x => x.Title).OneWay();
            set.Bind(body).For(v => v.text).To(x => x.Body).OneWay();
            set.Bind(closeButton).For(v => v.onClick).To(x => x.CloseCommand);
            set.Build();
        }

        private T FindDeep<T>(string childName) where T : Component
        {
            foreach (T c in GetComponentsInChildren<T>(true))
                if (c.name == childName) return c;
            Debug.LogError($"[NoticeView] 找不到名为 \"{childName}\" 的 {typeof(T).Name} 子节点（或在 Inspector 拖引用）", this);
            return null;
        }
    }
}
