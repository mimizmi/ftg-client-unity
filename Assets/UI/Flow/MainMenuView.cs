using Loxodon.Framework.Binding;
using Loxodon.Framework.Binding.Builder;
using UnityEngine;
using UnityEngine.UI;

namespace Domain.UI.Flow
{
    /// <summary>主菜单视图：两个按钮，命令绑定。Layer 应设为 Window。</summary>
    public sealed class MainMenuView : UIScreen
    {
        [Header("留空 = 按约定路径自动查找")]
        [SerializeField] private Button startButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private Button replayButton;   // 可选：不摆这个按钮就没有回放入口
        [SerializeField] private Button trainingButton; // 可选：不摆这个按钮就没有训练场入口

        private void Awake()
        {
            startButton = startButton != null ? startButton : Find<Button>("Start");
            quitButton = quitButton != null ? quitButton : Find<Button>("Quit");
            // Replay/Training 按钮是可选项：软查找，缺失不算错误
            if (replayButton == null)
                replayButton = transform.Find("Replay")?.GetComponent<Button>();
            if (trainingButton == null)
                trainingButton = transform.Find("Training")?.GetComponent<Button>();
        }

        public void Bind(MainMenuViewModel vm)
        {
            BindingSet<MainMenuView, MainMenuViewModel> set =
                this.CreateBindingSet<MainMenuView, MainMenuViewModel>(vm);
            set.Bind(startButton).For(v => v.onClick).To(x => x.StartCommand);
            set.Bind(quitButton).For(v => v.onClick).To(x => x.QuitCommand);
            if (replayButton != null)
                set.Bind(replayButton).For(v => v.onClick).To(x => x.ReplayCommand);
            if (trainingButton != null)
                set.Bind(trainingButton).For(v => v.onClick).To(x => x.TrainingCommand);
            set.Build();
        }

        private T Find<T>(string path) where T : Component
        {
            Transform child = transform.Find(path);
            if (child == null)
            {
                Debug.LogError($"[MainMenuView] 缺少子节点 \"{path}\"（或在 Inspector 拖引用）", this);
                return null;
            }
            var component = child.GetComponent<T>();
            if (component == null)
                Debug.LogError($"[MainMenuView] \"{path}\" 上缺少 {typeof(T).Name} 组件", child);
            return component;
        }
    }
}
