using System;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using UnityEngine;

namespace FTG.Tests
{
    /// <summary>
    /// headless 战斗装配工厂（复刻 BattleBootstrap.BuildPlayer，零 MonoBehaviour）。
    /// 新测试请用这里；BattleSimulationTests/RoundFlowTests 的内联版本历史遗留，逐步迁移。
    /// </summary>
    public static class TestBattleFactory
    {
        /// <summary>
        /// 测试用角色定义仓库：帧数据 JSON 直接从工程目录读文件
        /// （EditMode 测试的工作目录 = 工程根；不经 Addressables，保持 headless 确定性）。
        /// </summary>
        public static ExampleFighterDefinitionRepository CreateRepository()
            => new ExampleFighterDefinitionRepository(ReadBoxJson);

        private static string ReadBoxJson(string key)
        {
            string path = $"Assets/{key}.json"; // key 如 "BoxData/Frank_boxes" → Assets/BoxData/Frank_boxes.json
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }

        public static BattleSimulation Build(Func<int, ScriptedInput> p1Script,
            Func<int, ScriptedInput> p2Script, BattleConfig config = null,
            string characterId = "Frank")
        {
            FighterDefinition def = CreateRepository().Get(characterId);
            FighterState p1 = BuildFighter("P1", -1f, p1Script, def);
            FighterState p2 = BuildFighter("P2", 1f, p2Script, def);
            return new BattleSimulation(p1, p2, new CollisionResolver(), config);
        }

        public static FighterState BuildFighter(string name, float spawnX,
            Func<int, ScriptedInput> script, FighterDefinition def)
            => BuildFighterWithSeat(name, spawnX, new ScriptedSeat(script), def);

        /// <summary>注入任意座位（回放座位 / 假人座位）的装配变体。</summary>
        public static BattleSimulation BuildWithSeats(IInputSeat p1Seat, IInputSeat p2Seat,
            BattleConfig config = null, string characterId = "Frank")
        {
            FighterDefinition def = CreateRepository().Get(characterId);
            FighterState p1 = BuildFighterWithSeat("P1", -1f, p1Seat, def);
            FighterState p2 = BuildFighterWithSeat("P2", 1f, p2Seat, def);
            return new BattleSimulation(p1, p2, new CollisionResolver(), config);
        }

        public static FighterState BuildFighterWithSeat(string name, float spawnX,
            IInputSeat seat, FighterDefinition def)
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

        // ---- 共享状态哈希（FNV-1a；不用 string.GetHashCode——跨进程不稳定）----

        public static ulong HashState(BattleSimulation sim)
        {
            ulong h = 14695981039346656037UL;
            h = HashFighter(h, sim.P1);
            h = HashFighter(h, sim.P2);
            return h;
        }

        private static ulong HashFighter(ulong h, FighterState f)
        {
            // N2 定点化：直接哈希 Raw——定点没有 float 位模式的平台歧义，这正是迁移的意义
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
    }
}
