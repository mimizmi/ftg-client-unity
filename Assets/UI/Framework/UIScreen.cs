using UnityEngine;

namespace Domain.UI
{
    /// <summary>
    /// 所有受 UIManager 管理的界面的基类。生命周期：
    /// Open → OnOpened(arg) → OnFocus → …（被更高界面压栈时 OnBlur / 回到栈顶时 OnFocus）… → OnClosed → Destroy。
    /// 界面属于哪一层由 prefab 上的 layer 字段声明（数据驱动，调用方不传层参数）。
    /// </summary>
    public abstract class UIScreen : MonoBehaviour
    {
        [SerializeField] private UILayer layer = UILayer.Window;

        public UILayer Layer => layer;

        /// <summary>加载来源 key（资源释放用）。由 UIManager 写入。</summary>
        public string Key { get; internal set; }

        /// <summary>实例化后、入栈时调用一次。arg = Open 传入的任意参数。</summary>
        public virtual void OnOpened(object arg) { }

        /// <summary>成为本层栈顶（刚打开，或盖在上面的界面关掉了）。</summary>
        public virtual void OnFocus() { }

        /// <summary>被新界面盖住，不再是栈顶。</summary>
        public virtual void OnBlur() { }

        /// <summary>出栈时调用一次，随后 GameObject 被销毁。清理订阅放这里。</summary>
        public virtual void OnClosed() { }
    }
}
