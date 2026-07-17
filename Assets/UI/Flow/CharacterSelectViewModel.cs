using System;
using System.Collections.Generic;
using Loxodon.Framework.Commands;
using Loxodon.Framework.ViewModels;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 选人 VM。交互约定：依次点两下角色——第一下定 P1，第二下定 P2 并立即开战
    /// （双击同一角色即镜像内战）。角色列表数据驱动，加角色零代码改动。
    /// </summary>
    public sealed class CharacterSelectViewModel : ViewModelBase
    {
        public IReadOnlyList<string> Characters { get; }
        public ICommand BackCommand { get; }

        private string p1PickText = "P1: ---";
        private string p2PickText = "P2: ---";
        public string P1PickText { get => p1PickText; private set => Set(ref p1PickText, value); }
        public string P2PickText { get => p2PickText; private set => Set(ref p2PickText, value); }

        private readonly Action<string, string> onConfirm;
        private string p1Pick;

        public CharacterSelectViewModel(IReadOnlyList<string> characters,
            Action<string, string> onConfirm, Action onBack)
        {
            Characters = characters;
            this.onConfirm = onConfirm;
            BackCommand = new SimpleCommand(onBack);
        }

        /// <summary>由视图的角色按钮调用。</summary>
        public void Pick(string characterId)
        {
            if (p1Pick == null)
            {
                p1Pick = characterId;
                P1PickText = $"P1: {characterId}";
            }
            else
            {
                P2PickText = $"P2: {characterId}";
                onConfirm(p1Pick, characterId);
            }
        }
    }
}
