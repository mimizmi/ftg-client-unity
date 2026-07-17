using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 连段计数：命中时守方已在硬直 → 计数递增；守方脱离硬直无新命中 → 清零。
    /// 脚本采用"走近-点拳"循环：两拳之间持续贴近，抵消受击击退把守方推出射程。
    /// 若因间距/击退数据变化导致二连不成立，红的是这里而非计数逻辑——先查脚本再查代码。
    /// </summary>
    public class ComboTests
    {
        /// <summary>先走近 90 帧，之后一直贴身压制：按住前走，每 24 帧点一次轻拳。</summary>
        private static ScriptedInput WalkInMash(int frame)
        {
            if (frame <= 90) return new ScriptedInput(6);
            return new ScriptedInput(6, frame % 24 == 0 ? ButtonMask.LP : ButtonMask.None);
        }

        /// <summary>同上，但 300 帧后彻底收手（连段链应随守方硬直结束而断开）。</summary>
        private static ScriptedInput MashThenStop(int frame)
            => frame > 300 ? new ScriptedInput(5) : WalkInMash(frame);

        private static ScriptedInput Neutral(int _) => new ScriptedInput(5);

        [Test]
        public void Combo_ChainsWhileDefenderStunned()
        {
            BattleSimulation sim = TestBattleFactory.Build(WalkInMash, Neutral);

            int maxP1Combo = 0;
            int maxP2Combo = 0;
            for (int i = 0; i < 600; i++)
            {
                sim.Tick();
                if (sim.P1ComboHits > maxP1Combo) maxP1Combo = sim.P1ComboHits;
                if (sim.P2ComboHits > maxP2Combo) maxP2Combo = sim.P2ComboHits;
            }

            Assert.That(maxP1Combo, Is.GreaterThanOrEqualTo(2),
                "轻拳硬直 50 帧 > 24 帧压制节奏，第二拳应在硬直内命中形成连段");
            Assert.That(maxP2Combo, Is.EqualTo(0), "P2 全程没出过手，不该有连段数");
        }

        [Test]
        public void Combo_ResetsAfterStunEnds()
        {
            BattleSimulation sim = TestBattleFactory.Build(MashThenStop, Neutral);

            for (int i = 0; i < 450; i++) sim.Tick();

            // 最后一拳不晚于第 300 帧，硬直 50 帧 → 至迟 350 帧后链断
            Assert.That(sim.P1ComboHits, Is.EqualTo(0), "收手且守方脱离硬直后连段数必须清零");
            Assert.That(sim.P2.Status, Is.EqualTo(FighterStatus.Neutral));
        }
    }
}
