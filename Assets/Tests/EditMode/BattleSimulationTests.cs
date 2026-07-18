using System;
using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using NUnit.Framework;
using UnityEngine;

namespace FTG.Tests
{
    /// <summary>
    /// 确定性契约测试：同一输入脚本跑两遍，逐帧状态哈希必须完全一致。
    /// 这是回滚网络的地基——重模拟若不能逐比特复现，回滚就会 desync。
    /// 任何人往模拟里引入 Time/Random/字典遍历序依赖，这条测试就会红。
    /// </summary>
    public class BattleSimulationTests
    {
        private const int Frames = 600; // 10 秒战斗

        // ---- 输入脚本（纯函数：帧号 → 输入，两次运行天然相同）----

        private static ScriptedInput P1Script(int frame)
        {
            if (frame <= 90) return new ScriptedInput(6);                    // 走近
            if (frame % 12 == 0) return new ScriptedInput(5, ButtonMask.LP); // 点按轻拳
            return new ScriptedInput(5);
        }

        private static ScriptedInput P2Script(int frame)
        {
            if (frame <= 90) return new ScriptedInput(4);                  // 相向走近（P2 朝左，世界 4 = 前）
            if (frame > 200 && frame <= 320 && frame % 20 == 0)
                return new ScriptedInput(2, ButtonMask.LK);                // 蹲轻腿还手
            if (frame > 200 && frame <= 320) return new ScriptedInput(2);  // 按住下
            return new ScriptedInput(5);
        }

        // ---- 装配（复刻 BattleBootstrap.BuildPlayer，但零 MonoBehaviour）----

        private static FighterState BuildFighter(string name, float spawnX,
            ScriptedSeat seat, FighterDefinition def)
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

        private sealed class RunResult
        {
            public readonly List<ulong> FrameHashes = new List<ulong>(Frames);
            public int HitEventCount;
            public int MovesStarted;
            public BattleSimulation Simulation;
        }

        private static RunResult Run()
        {
            ExampleFighterDefinitionRepository repo = TestBattleFactory.CreateRepository();
            FighterDefinition def = repo.Get("Frank");

            FighterState p1 = BuildFighter("P1", -1f, new ScriptedSeat(P1Script), def);
            FighterState p2 = BuildFighter("P2", 1f, new ScriptedSeat(P2Script), def);

            var result = new RunResult
            {
                Simulation = new BattleSimulation(p1, p2, new CollisionResolver()),
            };
            result.Simulation.HitOccurred += _ => result.HitEventCount++;
            p1.MoveStarted += (_, _) => result.MovesStarted++;

            for (int i = 0; i < Frames; i++)
            {
                result.Simulation.Tick();
                result.FrameHashes.Add(HashState(result.Simulation));
            }
            return result;
        }

        // ---- 状态哈希（FNV-1a，全部确定性字段；不用 string.GetHashCode——跨进程不稳定）----

        private static ulong HashState(BattleSimulation sim)
        {
            ulong h = 14695981039346656037UL;
            h = HashFighter(h, sim.P1);
            h = HashFighter(h, sim.P2);
            return h;
        }

        private static ulong HashFighter(ulong h, FighterState f)
        {
            h = Fnv(h, (uint)f.Position.X.Raw);
            h = Fnv(h, (uint)f.Position.Y.Raw);
            h = Fnv(h, (uint)f.Health);
            h = Fnv(h, (byte)f.Status);
            h = Fnv(h, (uint)f.MoveFrame);
            h = Fnv(h, (uint)f.StunRemaining);
            h = Fnv(h, (byte)f.Movement.State);
            h = Fnv(h, (uint)f.Movement.MotionFrame);
            h = Fnv(h, f.FacingRight ? 1u : 0u);
            h = FnvString(h, f.CurrentMove?.MoveId);
            return h;
        }

        private static ulong Fnv(ulong h, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(value >> (i * 8));
                h *= 1099511628211UL;
            }
            return h;
        }

        private static ulong FnvString(ulong h, string s)
        {
            if (s == null) return Fnv(h, 0xFFFFFFFFu);
            for (int i = 0; i < s.Length; i++)
                h = Fnv(h, s[i]);
            return h;
        }

        // ---- 用例 ----

        [Test]
        public void SameInputs_TwoRuns_IdenticalFrameHashes()
        {
            RunResult a = Run();
            RunResult b = Run();

            Assert.That(a.FrameHashes.Count, Is.EqualTo(Frames));
            for (int i = 0; i < Frames; i++)
            {
                if (a.FrameHashes[i] != b.FrameHashes[i])
                    Assert.Fail($"第 {i + 1} 帧状态哈希不一致（首个分歧帧）：" +
                                $"{a.FrameHashes[i]:X16} ≠ {b.FrameHashes[i]:X16}。" +
                                "模拟里混入了非确定性源（Time/Random/遍历序/静态可变状态）。");
            }
        }

        [Test]
        public void ScriptedFight_ActuallyExercisesTheSim()
        {
            RunResult run = Run();

            // 反空转哨兵：哈希全程一致但什么都没发生的模拟没有意义
            Assert.That(run.Simulation.P1.Position.X, Is.Not.EqualTo(Fix.FromInt(-1)), "P1 应当走动过");
            Assert.That(run.MovesStarted, Is.GreaterThan(0), "P1 应当出过招");
            Assert.That(run.FrameHashes[0], Is.Not.EqualTo(run.FrameHashes[Frames - 1]),
                "首末帧状态应不同");
        }

        [Test]
        public void ScriptedFight_ProducesHitEvents()
        {
            // 验证完整攻防管线（判定框相交 → 裁决 → 事件发布）真的被走到。
            // 若此测试红而其余绿：脚本间距/判定框数据问题，不是确定性问题。
            RunResult run = Run();
            Assert.That(run.HitEventCount, Is.GreaterThan(0),
                "600 帧的近身互殴应当产生至少一次命中/拼招事件");
        }
    }
}
