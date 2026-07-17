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

        public ResultViewModel(int winner, Action onRematch, Action onMenu)
        {
            WinnerText = winner == 0 ? "DRAW" : $"PLAYER {winner} WINS";
            RematchCommand = new SimpleCommand(onRematch);
            MenuCommand = new SimpleCommand(onMenu);
        }
    }
}
