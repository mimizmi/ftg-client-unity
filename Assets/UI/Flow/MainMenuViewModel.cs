using System;
using Loxodon.Framework.Commands;
using Loxodon.Framework.ViewModels;

namespace Domain.UI.Flow
{
    /// <summary>主菜单 VM：只发意图，不知道流程细节（回调由 GameFlowController 注入）。</summary>
    public sealed class MainMenuViewModel : ViewModelBase
    {
        public ICommand StartCommand { get; }
        public ICommand ReplayCommand { get; }
        public ICommand QuitCommand { get; }

        public MainMenuViewModel(Action onStart, Action onQuit, Action onReplay)
        {
            StartCommand = new SimpleCommand(onStart);
            QuitCommand = new SimpleCommand(onQuit);
            ReplayCommand = new SimpleCommand(onReplay);
        }
    }
}
