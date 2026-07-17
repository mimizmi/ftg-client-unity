namespace Domain.UI
{
    /// <summary>
    /// UI 层级。每层一个独立 Canvas（sortingOrder = 枚举值 × 100），层内是栈语义：
    /// 新开的界面压栈获得焦点，关闭时弹栈把焦点还给下一个。
    /// </summary>
    public enum UILayer
    {
        /// <summary>战斗 HUD 等常驻显示。不参与焦点竞争。</summary>
        Hud = 0,

        /// <summary>全屏界面（主菜单、选人、结算）。</summary>
        Window = 1,

        /// <summary>弹窗（暂停菜单、确认框），盖在 Window 上。</summary>
        Popup = 2,

        /// <summary>加载遮罩（热更下载进度等）。</summary>
        Loading = 3,

        /// <summary>飘字/提示，最顶层，永不拦截输入。</summary>
        Toast = 4,
    }
}
