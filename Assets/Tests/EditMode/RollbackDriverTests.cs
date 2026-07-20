using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using Domain.Net;
using Domain.Net.Transport;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 回滚驱动的确定性护栏（P2 的判决性断言）：两端各跑一份 confirmed + predicted 模拟，经带延迟的
    /// 进程内链路交换真输入。核心主张——无论预测/回滚多频繁，两端 confirmed 轨迹逐位一致【且】等于单机
    /// 参照（两 ScriptedSeat 直接驱动同一批输入跑出的独立结果）。参照由完全独立的代码路径算出 = 真·独立校验。
    /// 与 Go 侧 lockstep 的 rollback_test 对称。
    /// </summary>
    public class RollbackDriverTests
    {
        // 脚本镜像 Go 侧 netcode_test：P1 前进逼近后连点 LP；P2 中途下蹲、也连点 LP。
        private static (byte dir, ButtonMask held) P1Script(int w)
        {
            if (w <= 30) return (6, ButtonMask.None);
            if (w % 6 == 0) return (5, ButtonMask.LP);
            return (5, ButtonMask.None);
        }

        private static (byte dir, ButtonMask held) P2Script(int w)
        {
            if (w >= 15 && w <= 25) return (2, ButtonMask.None);
            if (w % 7 == 0) return (5, ButtonMask.LP);
            return (5, ButtonMask.None);
        }

        private static BattleConfig Config() => new BattleConfig { IntroFrames = 0 };

        private static FighterDefinition Frank() => TestBattleFactory.CreateRepository().Get("Frank");

        private static FighterState BuildFighter(string name, float spawnX, IInputSeat seat, FighterDefinition def)
        {
            foreach (MotionPattern motion in def.Motions)
                seat.Detector.Add(motion);
            var moveTable = new MoveTable();
            moveTable.AddRange(def.MoveEntries);
            var fighter = new FighterState(seat, moveTable, def.Movement)
            {
                Name = name,
                Position = FixVec2.FromFloat(spawnX, 0f),
            };
            foreach (MoveData move in def.Moves)
                fighter.AddMove(move);
            fighter.SetReactions(def.ReactionMoves);
            return fighter;
        }

        private static BattleSimulation BuildNetworkSim(FighterDefinition def)
        {
            FighterState p1 = BuildFighter("P1", -1f, new NetworkSeat(), def);
            FighterState p2 = BuildFighter("P2", 1f, new NetworkSeat(), def);
            return new BattleSimulation(p1, p2, new CollisionResolver(), Config());
        }

        // 单机参照：两 ScriptedSeat 直接驱动，逐帧 StateHash（D=0，帧 f 用脚本第 f 次采样）。
        private static List<ulong> ReferenceTrace(FighterDefinition def, int n)
        {
            FighterState p1 = BuildFighter("P1", -1f,
                new ScriptedSeat(f => { var (d, h) = P1Script(f); return new ScriptedInput(d, h); }), def);
            FighterState p2 = BuildFighter("P2", 1f,
                new ScriptedSeat(f => { var (d, h) = P2Script(f); return new ScriptedInput(d, h); }), def);
            var sim = new BattleSimulation(p1, p2, new CollisionResolver(), Config());

            var trace = new List<ulong>(n);
            for (int f = 1; f <= n; f++)
            {
                sim.Tick();
                trace.Add(StateHasher.HashState(sim));
            }
            return trace;
        }

        [Test]
        public void ConfirmedTrace_MatchesReference_UnderPredictionAndRollback()
        {
            const int n = 120;
            const int latency = 3;
            FighterDefinition def = Frank();

            List<ulong> reference = ReferenceTrace(def, n);
            Assert.That(Distinct(reference), Is.GreaterThan(1), "参照轨迹应非平凡（状态确有变化）");

            var (ta, tb, aToB, bToA) = LoopbackLink.NewPair(latency);
            var driverA = new RollbackDriver(BuildNetworkSim(def), ta,
                w => { var (d, h) = P1Script(w); return new LocalInput(d, h); }, localIsP1: true);
            var driverB = new RollbackDriver(BuildNetworkSim(def), tb,
                w => { var (d, h) = P2Script(w); return new LocalInput(d, h); }, localIsP1: false);

            int steps = 0;
            int limit = n + 2 * latency + 32;
            while ((driverA.ConfirmedFrame < n || driverB.ConfirmedFrame < n) && steps < limit)
            {
                driverA.Advance();
                driverB.Advance();
                aToB.Step();
                bToA.Step();
                steps++;
            }

            Assert.That(driverA.ConfirmedFrame, Is.GreaterThanOrEqualTo(n), "A 未在上限内确认到目标帧");
            Assert.That(driverB.ConfirmedFrame, Is.GreaterThanOrEqualTo(n), "B 未在上限内确认到目标帧");

            AssertTrace("A", driverA.ConfirmedTrace, reference, n);
            AssertTrace("B", driverB.ConfirmedTrace, reference, n);

            // 回滚确有发生：预测窗口 ≥ 1，且确有误预测被真输入修正（否则“回滚”无从证明）。
            Assert.That(driverA.MaxRollback, Is.GreaterThanOrEqualTo(1), "A 应有预测/回滚窗口");
            Assert.That(driverB.MaxRollback, Is.GreaterThanOrEqualTo(1), "B 应有预测/回滚窗口");
            Assert.That(driverA.Corrections + driverB.Corrections, Is.GreaterThan(0),
                "脚本会变，预测必有被真输入修正的帧（证明回滚真的在工作）");
        }

        [Test]
        public void ZeroLatency_StillMatchesReference()
        {
            const int n = 90;
            FighterDefinition def = Frank();
            List<ulong> reference = ReferenceTrace(def, n);

            var (ta, tb, aToB, bToA) = LoopbackLink.NewPair(0);
            var driverA = new RollbackDriver(BuildNetworkSim(def), ta,
                w => { var (d, h) = P1Script(w); return new LocalInput(d, h); }, localIsP1: true);
            var driverB = new RollbackDriver(BuildNetworkSim(def), tb,
                w => { var (d, h) = P2Script(w); return new LocalInput(d, h); }, localIsP1: false);

            int steps = 0;
            int limit = n + 32;
            while ((driverA.ConfirmedFrame < n || driverB.ConfirmedFrame < n) && steps < limit)
            {
                driverA.Advance();
                driverB.Advance();
                aToB.Step();
                bToA.Step();
                steps++;
            }

            AssertTrace("A", driverA.ConfirmedTrace, reference, n);
            AssertTrace("B", driverB.ConfirmedTrace, reference, n);
        }

        private static void AssertTrace(string who, IReadOnlyList<ulong> got, List<ulong> reference, int n)
        {
            Assert.That(got.Count, Is.GreaterThanOrEqualTo(n), $"{who} 确认轨迹帧数不足");
            for (int i = 0; i < n; i++)
                Assert.That(got[i], Is.EqualTo(reference[i]),
                    $"{who} 帧 {i + 1} 哈希与单机参照分歧（desync）");
        }

        private static int Distinct(List<ulong> xs) => new HashSet<ulong>(xs).Count;
    }
}
