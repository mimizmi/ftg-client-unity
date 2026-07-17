using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// AI 假人座位与训练规则的行为验证。
    /// 核心主张：AI 走与玩家完全相同的输入管线，且策略是纯函数 → 整局确定性。
    /// </summary>
    public sealed class AiSeatTests
    {
        private const int Frames = 900; // 15 秒：足够 CPU 走近并戳到人

        private static BattleSimulation BuildCpuBattle(out AiSeat ai, BattleConfig config = null)
        {
            var p1 = new ScriptedSeat(_ => new ScriptedInput(5)); // P1 站桩挨打
            ai = new AiSeat(DummyPolicies.SimpleCpu);
            BattleSimulation sim = TestBattleFactory.BuildWithSeats(p1, ai, config);
            ai.Attach(self: sim.P2, opponent: sim.P1); // 座位先于角色构造，装配后接线
            return sim;
        }

        [Test]
        public void SimpleCpu_TwoRuns_IdenticalFrameHashes()
        {
            BattleSimulation a = BuildCpuBattle(out _);
            BattleSimulation b = BuildCpuBattle(out _);

            for (int i = 0; i < Frames; i++)
            {
                a.Tick();
                b.Tick();
                Assert.That(TestBattleFactory.HashState(a), Is.EqualTo(TestBattleFactory.HashState(b)),
                    $"第 {i} 帧状态哈希分叉——AI 策略里混进了非确定性来源");
            }
        }

        [Test]
        public void SimpleCpu_WalksInAndLandsHits()
        {
            // 用训练配置（超长回合）：不会中途 KO 重置血量，断言干净
            BattleSimulation sim = BuildCpuBattle(out _, TrainingRules.CreateConfig());
            for (int i = 0; i < Frames; i++) sim.Tick();

            Assert.That(sim.P1.Health, Is.LessThan(sim.Config.MaxHealth),
                "CPU 在 15 秒内应至少命中站桩的 P1 一次（走近 + 节拍戳拳全链路）");
        }

        [Test]
        public void TrainingRules_RefillsHealth_AfterComboEnds()
        {
            BattleSimulation sim = BuildCpuBattle(out AiSeat ai, TrainingRules.CreateConfig());
            using (new TrainingRules(sim))
            {
                bool everDamaged = false;
                for (int i = 0; i < Frames; i++)
                {
                    sim.Tick();
                    if (sim.P1.Health < sim.Config.MaxHealth) everDamaged = true;
                }
                Assert.That(everDamaged, Is.True, "前置条件：训练期间 P1 必须挨过打");

                ai.Policy = DummyPolicies.Idle;          // CPU 收手（训练场 F1 的语义）
                for (int i = 0; i < 300; i++) sim.Tick(); // 5 秒足够脱离硬直回中立

                Assert.That(sim.P1.Health, Is.EqualTo(sim.Config.MaxHealth),
                    "双方回到中立后训练规则应把血量回满");
            }
        }
    }
}
