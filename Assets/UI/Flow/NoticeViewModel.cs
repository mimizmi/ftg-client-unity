using System;
using Loxodon.Framework.Commands;
using Loxodon.Framework.ViewModels;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 公告窗 VM：内容与关闭行为全部由调用方注入——文本来自 Lua 表，
    /// 关闭回调回到 Lua。VM 不知道 Lua 存在（也不该知道），它只是数据的搬运工。
    /// </summary>
    public sealed class NoticeViewModel : ViewModelBase
    {
        public string Title { get; }
        public string Body { get; }
        public SimpleCommand CloseCommand { get; }

        public NoticeViewModel(string title, string body, Action onClose)
        {
            Title = title;
            Body = body;
            CloseCommand = new SimpleCommand(onClose);
        }
    }
}
