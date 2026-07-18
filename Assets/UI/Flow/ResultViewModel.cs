using System;
using Loxodon.Framework.Commands;
using Loxodon.Framework.ViewModels;

namespace Domain.UI.Flow
{
    /// <summary>结算 VM：胜者文案 + 再战/回菜单两个意图。</summary>
    public sealed class ResultViewModel : ViewModelBase
    {
        public string WinnerText { get; }
        public ICommand RematchCommand { get; }
        public ICommand MenuCommand { get; }

        // 胜者文案由调用方本地化后传入：VM 不依赖本地化服务，保持纯数据搬运
        public ResultViewModel(string winnerText, Action onRematch, Action onMenu)
        {
            WinnerText = winnerText;
            RematchCommand = new SimpleCommand(onRematch);
            MenuCommand = new SimpleCommand(onMenu);
        }
    }
}
