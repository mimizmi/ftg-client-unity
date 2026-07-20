using System;
using Loxodon.Framework.Commands;
using Loxodon.Framework.ViewModels;

namespace Domain.UI.Flow
{
    /// <summary>主菜单 VM：只发意图，不知道流程细节（回调由 GameFlowController 注入）。</summary>
    public sealed class MainMenuViewModel : ViewModelBase
    {
        public ICommand StartCommand { get; }
        public ICommand OnlineCommand { get; }
        public ICommand TrainingCommand { get; }
        public ICommand ReplayCommand { get; }
        public ICommand QuitCommand { get; }
        public ICommand LanguageCommand { get; }

        public MainMenuViewModel(Action onStart, Action onQuit, Action onReplay, Action onTraining,
            Action onLanguage, Action onOnline = null)
        {
            StartCommand = new SimpleCommand(onStart);
            QuitCommand = new SimpleCommand(onQuit);
            ReplayCommand = new SimpleCommand(onReplay);
            TrainingCommand = new SimpleCommand(onTraining);
            LanguageCommand = new SimpleCommand(onLanguage);
            OnlineCommand = new SimpleCommand(onOnline ?? (() => { }));
        }
    }
}
