using System;
using Domain.Infrastructure.Battle;
using Loxodon.Framework.ViewModels;
using UnityEngine;

namespace Domain.UI.Battle
{
    /// <summary>
    /// 战斗 HUD 的 ViewModel：BattleSimulation 的【只读观察者】。
    /// 订阅模拟事件 → 转成可绑定属性；绝不反向写模拟（确定性纪律的 UI 侧边界）。
    /// 属性只在值变化时触发通知（ObservableObject.Set 内置去重），60Hz 轮询不产生绑定风暴。
    /// </summary>
    public sealed class BattleHudViewModel : ViewModelBase, IDisposable
    {
        private readonly BattleSimulation sim;

        private float p1HealthRatio = 1f, p2HealthRatio = 1f;
        private string timerText = "99";
        private string p1WinsText = "0", p2WinsText = "0";
        private string p1ComboText = "", p2ComboText = "";
        private bool p1ComboVisible, p2ComboVisible;
        private string announcementText = "";
        private bool announcementVisible;

        // 播报的显示倒计时（帧）。-1 = 常驻直到被下一条覆盖。纯表现状态，不进模拟。
        private int announceFramesLeft;
        private BattlePhase lastPhase;

        public float P1HealthRatio { get => p1HealthRatio; private set => Set(ref p1HealthRatio, value); }
        public float P2HealthRatio { get => p2HealthRatio; private set => Set(ref p2HealthRatio, value); }
        public string TimerText { get => timerText; private set => Set(ref timerText, value); }
        public string P1WinsText { get => p1WinsText; private set => Set(ref p1WinsText, value); }
        public string P2WinsText { get => p2WinsText; private set => Set(ref p2WinsText, value); }
        public string P1ComboText { get => p1ComboText; private set => Set(ref p1ComboText, value); }
        public string P2ComboText { get => p2ComboText; private set => Set(ref p2ComboText, value); }
        public bool P1ComboVisible { get => p1ComboVisible; private set => Set(ref p1ComboVisible, value); }
        public bool P2ComboVisible { get => p2ComboVisible; private set => Set(ref p2ComboVisible, value); }
        public string AnnouncementText { get => announcementText; private set => Set(ref announcementText, value); }
        public bool AnnouncementVisible { get => announcementVisible; private set => Set(ref announcementVisible, value); }

        public BattleHudViewModel(BattleSimulation sim)
        {
            this.sim = sim;
            lastPhase = sim.Phase;

            sim.TickFinished += OnTick;
            sim.RoundStarted += OnRoundStarted;
            sim.RoundEnded += OnRoundEnded;
            sim.MatchEnded += OnMatchEnded;

            Refresh();
            if (sim.Phase == BattlePhase.Intro)
                Show($"ROUND {sim.RoundNumber}", persist: true);
        }

        public void Dispose()
        {
            sim.TickFinished -= OnTick;
            sim.RoundStarted -= OnRoundStarted;
            sim.RoundEnded -= OnRoundEnded;
            sim.MatchEnded -= OnMatchEnded;
        }

        private void OnTick(int frame)
        {
            // 回合切换检测：RoundOver → Intro 的那一帧亮出新回合号
            if (sim.Phase != lastPhase)
            {
                if (sim.Phase == BattlePhase.Intro)
                    Show($"ROUND {sim.RoundNumber}", persist: true);
                lastPhase = sim.Phase;
            }

            if (announceFramesLeft > 0 && --announceFramesLeft == 0)
                AnnouncementVisible = false;

            Refresh();
        }

        private void OnRoundStarted(int round) => Show("FIGHT!", frames: 45);

        private void OnRoundEnded(RoundResult result)
            => Show(result.ByTimeout ? "TIME UP" : "K.O.", persist: true);

        private void OnMatchEnded(int winner)
            => Show(winner == 0 ? "DRAW" : $"PLAYER {winner} WINS", persist: true);

        private void Show(string text, int frames = 0, bool persist = false)
        {
            AnnouncementText = text;
            AnnouncementVisible = true;
            announceFramesLeft = persist ? -1 : frames;
        }

        private void Refresh()
        {
            int maxHealth = sim.Config.MaxHealth;
            P1HealthRatio = Mathf.Clamp01((float)sim.P1.Health / maxHealth);
            P2HealthRatio = Mathf.Clamp01((float)sim.P2.Health / maxHealth);
            TimerText = sim.RoundSecondsRemaining.ToString();
            P1WinsText = sim.P1Wins.ToString();
            P2WinsText = sim.P2Wins.ToString();

            P1ComboVisible = sim.P1ComboHits >= 2;
            P2ComboVisible = sim.P2ComboHits >= 2;
            if (P1ComboVisible) P1ComboText = $"{sim.P1ComboHits} HITS";
            if (P2ComboVisible) P2ComboText = $"{sim.P2ComboHits} HITS";
        }
    }
}
