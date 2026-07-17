using System;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 训练场规则：双方都回到中立（连段结束）时把血量回满——训练场永不 KO。
    /// 挂 TickFinished 而非 Messenger：**改动战斗状态的逻辑必须走这个 C# 事件**
    /// （帧内顺序确定，回滚网络的前提；Messenger 只许只读观察）——这是本工程红线，
    /// AI 假人与训练规则是它的前两个正当使用者。
    /// </summary>
    public sealed class TrainingRules : IDisposable
    {
        private readonly BattleSimulation sim;

        public TrainingRules(BattleSimulation sim)
        {
            this.sim = sim;
            sim.TickFinished += OnTick;
        }

        /// <summary>训练场战斗配置：超长回合 + 打不完的胜场数（其余沿用默认）。</summary>
        public static BattleConfig CreateConfig() => new BattleConfig
        {
            RoundFrames = 99 * 60 * 60, // 99 分钟
            RoundsToWin = 99,           // 理论上到不了
        };

        private void OnTick(int frame)
        {
            FighterState p1 = sim.P1;
            FighterState p2 = sim.P2;

            // 连段进行中不回血（连击伤害要看得见）；双方归于平静才结账
            if (p1.Status != FighterStatus.Neutral || p2.Status != FighterStatus.Neutral) return;

            int max = sim.Config.MaxHealth;
            if (p1.Health != max) p1.Health = max;
            if (p2.Health != max) p2.Health = max;
        }

        public void Dispose() => sim.TickFinished -= OnTick;
    }
}
