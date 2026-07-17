using TMPro;
using UnityEngine;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 加载指示界面（挂 Loading 层）。没有 ViewModel——它没有业务状态可绑定，只有动画点点。
    /// M3-3 热更接入后升级：显示 catalog 校验 / 增量下载进度与字节数。
    /// </summary>
    public sealed class LoadingView : UIScreen
    {
        [SerializeField] private TMP_Text label; // 可空：不拖则自动找名为 "Label" 的子物体

        private string status; // 非空时替代点点动画（热更检查/下载进度等具体状态）

        /// <summary>显示具体状态文本（传 null 恢复 LOADING 点点动画）。</summary>
        public void SetStatus(string text) => status = text;

        public override void OnOpened(object arg)
        {
            base.OnOpened(arg);
            if (label == null) label = transform.Find("Label")?.GetComponent<TMP_Text>();
        }

        private void Update()
        {
            if (label == null) return;
            int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
            label.text = status ?? ("LOADING" + new string('.', dots));
        }
    }
}
