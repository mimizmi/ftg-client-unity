using Loxodon.Framework.Binding;
using Loxodon.Framework.Binding.Builder;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 选人视图：角色按钮不是预摆的——以 "Entry" 为模板按 VM 的角色列表运行时生成，
    /// 加角色不动 prefab（数据驱动）。Layer 应设为 Window。
    /// </summary>
    public sealed class CharacterSelectView : UIScreen
    {
        [Header("留空 = 按约定路径自动查找")]
        [SerializeField] private Button entryTemplate; // 名为 "Entry" 的模板按钮（保持未激活）
        [SerializeField] private TMP_Text p1PickText;
        [SerializeField] private TMP_Text p2PickText;
        [SerializeField] private Button backButton;

        private void Awake()
        {
            entryTemplate = entryTemplate != null ? entryTemplate : Find<Button>("Entry");
            p1PickText = p1PickText != null ? p1PickText : Find<TMP_Text>("P1Pick");
            p2PickText = p2PickText != null ? p2PickText : Find<TMP_Text>("P2Pick");
            backButton = backButton != null ? backButton : Find<Button>("Back");
        }

        public void Bind(CharacterSelectViewModel vm)
        {
            BindingSet<CharacterSelectView, CharacterSelectViewModel> set =
                this.CreateBindingSet<CharacterSelectView, CharacterSelectViewModel>(vm);
            set.Bind(p1PickText).For(v => v.text).To(x => x.P1PickText).OneWay();
            set.Bind(p2PickText).For(v => v.text).To(x => x.P2PickText).OneWay();
            set.Bind(backButton).For(v => v.onClick).To(x => x.BackCommand);
            set.Build();

            BuildEntries(vm);
        }

        private void BuildEntries(CharacterSelectViewModel vm)
        {
            if (entryTemplate == null) return;
            entryTemplate.gameObject.SetActive(false);

            foreach (string id in vm.Characters)
            {
                Button entry = Instantiate(entryTemplate, entryTemplate.transform.parent);
                entry.name = $"Entry_{id}";
                var label = entry.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = id;

                string captured = id; // 闭包逐项捕获
                entry.onClick.AddListener(() => vm.Pick(captured));
                entry.gameObject.SetActive(true);
            }
        }

        private T Find<T>(string path) where T : Component
        {
            Transform child = transform.Find(path);
            if (child == null)
            {
                Debug.LogError($"[CharacterSelectView] 缺少子节点 \"{path}\"（或在 Inspector 拖引用）", this);
                return null;
            }
            var component = child.GetComponent<T>();
            if (component == null)
                Debug.LogError($"[CharacterSelectView] \"{path}\" 上缺少 {typeof(T).Name} 组件", child);
            return component;
        }
    }
}
