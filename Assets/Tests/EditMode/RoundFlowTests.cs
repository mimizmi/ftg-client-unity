using System;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using NUnit.Framework;
using UnityEngine;

namespace FTG.Tests
{
    /// <summary>回合系统：KO/超时判决、胜场累计、回合重置、比赛终局、开场冻结。</summary>
    public class RoundFlowTests
    {
        // ---- 输入脚本 ----

        private static ScriptedInput WalkThenMash(int frame)
        {
            if (frame <= 90) return new ScriptedInput(6);
            return frame % 12 == 0
                ? new ScriptedInput(5, ButtonMask.LP)
                : new ScriptedInput(5);
        }

        private static ScriptedInput WalkThenNeutral(int frame)
            => frame <= 90 ? new ScriptedInput(4) : new ScriptedInput(5);

        private static ScriptedInput Neutral(int _) => new ScriptedInput(5);

        private static ScriptedInput HoldForward(int _) => new ScriptedInput(6);

        // ---- 装配 ----

        private static BattleSimulation Build(Func<int, ScriptedInput> p1Script,
            Func<int, ScriptedInput> p2Script, BattleConfig config)
        {
            FighterDefinition def = TestBattleFactory.CreateRepository().Get("Frank");
            FighterState p1 = BuildFighter("P1", -1f, p1Script, def);
            FighterState p2 = BuildFighter("P2", 1f, p2Script, def);
            return new BattleSimulation(p1, p2, new CollisionResolver(), config);
        }

        private static FighterState BuildFighter(string name, float spawnX,
            Func<int, ScriptedInput> script, FighterDefinition def)
        {
            var seat = new ScriptedSeat(script);
            foreach (MotionPattern motion in def.Motions)
                seat.Detector.Add(motion);

            var moveTable = new MoveTable();
            moveTable.AddRange(def.MoveEntries);

            var fighter = new FighterState(seat, moveTable, def.Movement)
            {
                Name = name,
                Position = new Vector2(spawnX, 0f),
            };
            foreach (MoveData move in def.Moves)
                fighter.AddMove(move);
            fighter.SetReactions(def.ReactionMoves);
            return fighter;
        }

        private static void TickUntil(BattleSimulation sim, Func<bool> done,
            int maxFrames, string what)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                sim.Tick();
                if (done()) return;
            }
            Assert.Fail($"{maxFrames} 帧内未发生：{what}");
        }

        // ---- 用例 ----

        [Test]
        public void KO_AwardsWin_ThenResetsNextRound()
        {
            var config = new BattleConfig { RoundOverFrames = 10 };
            BattleSimulation sim = Build(WalkThenMash, WalkThenNeutral, config);
            sim.P2.Health = 30; // 一发轻拳（30 伤害）即 KO

            RoundResult? result = null;
            sim.RoundEnded += r => result = r;

            TickUntil(sim, () => result != null, 1200, "KO 回合判决");

            Assert.That(result.Value.Winner, Is.EqualTo(1));
            Assert.That(result.Value.ByTimeout, Is.False);
            Assert.That(sim.P1Wins, Is.EqualTo(1));
            Assert.That(sim.P2Wins, Is.EqualTo(0));
            Assert.That(sim.Phase, Is.EqualTo(BattlePhase.RoundOver));

            // 定格结束 → 第二回合归位满血重开
            int startedRound = 0;
            sim.RoundStarted += n => startedRound = n;
            TickUntil(sim, () => sim.Phase == BattlePhase.Fighting, 20, "下一回合开打");

            Assert.That(sim.RoundNumber, Is.EqualTo(2));
            Assert.That(startedRound, Is.EqualTo(2));
            Assert.That(sim.P2.Health, Is.EqualTo(config.MaxHealth));
            Assert.That(sim.P1.Health, Is.EqualTo(config.MaxHealth));
            Assert.That(sim.P1.Status, Is.EqualTo(FighterStatus.Neutral));
            Assert.That(sim.P2.Status, Is.EqualTo(FighterStatus.Neutral));
            Assert.That(sim.RoundFramesRemaining,
                Is.EqualTo(config.RoundFrames).Within(1), "计时器应重置");
        }

        [Test]
        public void Timeout_HigherHealthWins()
        {
            var config = new BattleConfig { RoundFrames = 30, RoundOverFrames = 10 };
            BattleSimulation sim = Build(Neutral, Neutral, config);
            sim.P2.Health = 400;

            RoundResult? result = null;
            sim.RoundEnded += r => result = r;

            TickUntil(sim, () => result != null, 100, "超时判决");

            Assert.That(result.Value.ByTimeout, Is.True);
            Assert.That(result.Value.Winner, Is.EqualTo(1), "时间到血多者胜");
            Assert.That(sim.P1Wins, Is.EqualTo(1));
            Assert.That(sim.P2Wins, Is.EqualTo(0));
        }

        [Test]
        public void Timeout_EqualHealth_DrawScoresBoth()
        {
            var config = new BattleConfig { RoundFrames = 10, RoundOverFrames = 10 };
            BattleSimulation sim = Build(Neutral, Neutral, config);

            RoundResult? result = null;
            sim.RoundEnded += r => result = r;

            TickUntil(sim, () => result != null, 50, "平局判决");

            Assert.That(result.Value.Winner, Is.EqualTo(0));
            Assert.That(sim.P1Wins, Is.EqualTo(1), "平局双方各记一胜（双 KO 规则）");
            Assert.That(sim.P2Wins, Is.EqualTo(1));
        }

        [Test]
        public void Match_EndsAfterRoundsToWin()
        {
            var config = new BattleConfig { RoundsToWin = 1, RoundOverFrames = 10 };
            BattleSimulation sim = Build(WalkThenMash, WalkThenNeutral, config);
            sim.P2.Health = 30;

            int matchWinner = -1;
            sim.MatchEnded += w => matchWinner = w;

            TickUntil(sim, () => sim.Phase == BattlePhase.MatchOver, 1200, "比赛终局");

            Assert.That(matchWinner, Is.EqualTo(1));
            Assert.That(sim.RoundNumber, Is.EqualTo(1), "一局定胜负，不应开第二回合");

            // 终态稳定：继续推帧不产生任何新状态
            int frameBefore = sim.CurrentFrame;
            sim.Tick();
            Assert.That(sim.Phase, Is.EqualTo(BattlePhase.MatchOver));
            Assert.That(sim.CurrentFrame, Is.EqualTo(frameBefore + 1), "时钟仍走，战斗不动");
        }

        [Test]
        public void Intro_FreezesFightersUntilRoundStart()
        {
            var config = new BattleConfig { IntroFrames = 30 };
            BattleSimulation sim = Build(HoldForward, Neutral, config);

            bool started = false;
            sim.RoundStarted += _ => started = true;

            // 冻结期：一直按前也不许动
            for (int i = 0; i < 29; i++) sim.Tick();
            Assert.That(started, Is.False);
            Assert.That(sim.P1.Position.x, Is.EqualTo(-1f), "Intro 期间必须冻结");

            // 开打后走 60 帧必然位移
            TickUntil(sim, () => started, 5, "RoundStarted");
            for (int i = 0; i < 60; i++) sim.Tick();
            Assert.That(sim.P1.Position.x, Is.Not.EqualTo(-1f), "开打后应能走动");
        }
    }
}
