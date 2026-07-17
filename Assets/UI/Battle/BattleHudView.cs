using Domain.UI;
using Loxodon.Framework.Binding;
using Loxodon.Framework.Binding.Builder;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI.Battle
{
    /// <summary>
    /// 战斗 HUD 视图：零逻辑，纯声明式绑定（Loxodon BindingSet，全部 OneWay，
    /// UI 是模拟的只读观察者）。控件引用优先用 Inspector 拖拽；留空则按约定
    /// 路径自动查找（见 AutoWire 中的路径表），报错信息即搭建说明书。
    /// </summary>
    public sealed class BattleHudView : UIScreen
    {
        [Header("留空 = 按约定路径自动查找")]
        [SerializeField] private Image p1HealthFill;
        [SerializeField] private Image p2HealthFill;
        [SerializeField] private TMP_Text timerText;
        [SerializeField] private TMP_Text p1WinsText;
        [SerializeField] private TMP_Text p2WinsText;
        [SerializeField] private TMP_Text p1ComboText;
        [SerializeField] private TMP_Text p2ComboText;
        [SerializeField] private TMP_Text announcementText;

        private BattleHudViewModel viewModel;

        private void Awake() => AutoWire();

        public override void OnOpened(object arg)
        {
            base.OnOpened(arg);

            // HUD 纯显示、零交互（全 OneWay 绑定）：关掉全部 Graphic 的射线目标，
            // GraphicRaycaster 不再逐帧遍历它们（层级 Canvas 的 raycaster 也已被 UIManager 关掉，双保险）
            foreach (Graphic g in GetComponentsInChildren<Graphic>(true))
                g.raycastTarget = false;

            // 动静分离：高频变化的控件各自套一层嵌套 Canvas——它们脏了只重建自己
            // 的小网格，不再牵连整张 HUD 画布重建/重新合批（代价是各多一个 draw call，
            // HUD 元素个位数，这笔账划算；验证见 docs/PERFORMANCE.md 的 UI 章节）
            IsolateDynamic(timerText);
            IsolateDynamic(p1ComboText);
            IsolateDynamic(p2ComboText);
            IsolateDynamic(announcementText);
            IsolateDynamic(p1HealthFill);
            IsolateDynamic(p2HealthFill);
        }

        private static void IsolateDynamic(Component c)
        {
            if (c == null || c.GetComponent<Canvas>() != null) return;
            c.gameObject.AddComponent<Canvas>(); // 不 overrideSorting：继承父画布排序，仅隔离重建域
        }

        /// <summary>由 BattleHudBinder 在模拟就绪后调用。</summary>
        public void Bind(BattleHudViewModel vm)
        {
            viewModel = vm;

            BindingSet<BattleHudView, BattleHudViewModel> set =
                this.CreateBindingSet<BattleHudView, BattleHudViewModel>(vm);

            set.Bind(p1HealthFill).For(v => v.fillAmount).To(x => x.P1HealthRatio).OneWay();
            set.Bind(p2HealthFill).For(v => v.fillAmount).To(x => x.P2HealthRatio).OneWay();
            set.Bind(timerText).For(v => v.text).To(x => x.TimerText).OneWay();
            set.Bind(p1WinsText).For(v => v.text).To(x => x.P1WinsText).OneWay();
            set.Bind(p2WinsText).For(v => v.text).To(x => x.P2WinsText).OneWay();

            set.Bind(p1ComboText).For(v => v.text).To(x => x.P1ComboText).OneWay();
            set.Bind(p1ComboText).For(v => v.enabled).To(x => x.P1ComboVisible).OneWay();
            set.Bind(p2ComboText).For(v => v.text).To(x => x.P2ComboText).OneWay();
            set.Bind(p2ComboText).For(v => v.enabled).To(x => x.P2ComboVisible).OneWay();

            set.Bind(announcementText).For(v => v.text).To(x => x.AnnouncementText).OneWay();
            set.Bind(announcementText).For(v => v.enabled).To(x => x.AnnouncementVisible).OneWay();

            set.Build();
        }

        public override void OnClosed()
        {
            viewModel?.Dispose(); // 解除对模拟事件的订阅
            viewModel = null;
        }

        private void AutoWire()
        {
            p1HealthFill = p1HealthFill != null ? p1HealthFill : Find<Image>("P1HealthBar/Fill");
            p2HealthFill = p2HealthFill != null ? p2HealthFill : Find<Image>("P2HealthBar/Fill");
            timerText = timerText != null ? timerText : Find<TMP_Text>("Timer");
            p1WinsText = p1WinsText != null ? p1WinsText : Find<TMP_Text>("P1Wins");
            p2WinsText = p2WinsText != null ? p2WinsText : Find<TMP_Text>("P2Wins");
            p1ComboText = p1ComboText != null ? p1ComboText : Find<TMP_Text>("P1Combo");
            p2ComboText = p2ComboText != null ? p2ComboText : Find<TMP_Text>("P2Combo");
            announcementText = announcementText != null ? announcementText : Find<TMP_Text>("Announcement");
        }

        private T Find<T>(string path) where T : Component
        {
            Transform child = transform.Find(path);
            if (child == null)
            {
                Debug.LogError($"[BattleHudView] 缺少子节点 \"{path}\"（或在 Inspector 拖引用）", this);
                return null;
            }
            var component = child.GetComponent<T>();
            if (component == null)
                Debug.LogError($"[BattleHudView] \"{path}\" 上缺少 {typeof(T).Name} 组件", child);
            return component;
        }
    }
}
