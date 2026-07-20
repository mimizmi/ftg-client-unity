using System;
using System.Collections.Generic;
using System.Threading;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using Domain.Net;
using Domain.Net.Transport;
using NUnit.Framework;
using Proto = FTG.Net.Proto;

namespace FTG.Tests
{
    /// <summary>
    /// 北极星终极证明（P4）：一个【C# Unity 客户端】与一个【Go cmd/client】经同一个 Go 中继服务器
    /// 对打一局，C# 侧的 confirmed 轨迹逐帧逐位等于单机参照——而 Go 侧独立地也等于同一参照
    /// （Go netcode_test 已证），于是两种语言、两套独立实现的确定性回滚在真 UDP 下逐帧哈希一致。
    ///
    /// 这是【实机联机对拍】：N4 的离线跨语言对拍在这里升级成真 socket、真服务器、真对家。
    /// 自检方式：本端 confirmed（由服务器转发的 Go 机器人真输入驱动）逐位 == 本地独立算出的参照，
    /// 二者用完全不同的代码路径，等号成立即证同步无 desync。
    ///
    /// 手动运行（本测试标 [Explicit]，且无服务器/对家时自动 Ignore，绝不卡 CI）：
    ///   1) go run ./cmd/server -addr :7777
    ///   2) go run ./cmd/client -server 127.0.0.1:7777 -frames 600   （Go 机器人占另一座位）
    ///   3) 在 Unity Test Runner 里单独运行本测试
    /// 环境变量可覆盖：FTG_SERVER（默认 127.0.0.1:7777）、FTG_FRAMES（默认 180）。
    /// </summary>
    public class CrossLanguageLiveRollbackTests
    {
        // 与 Go cmd/client 的 scriptForSeat 逐字一致：P1 前进逼近后连点 LP；P2 中途下蹲、也连点 LP。
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

        private static (byte dir, ButtonMask held) ScriptForSeat(int seat, int w)
            => seat == 1 ? P1Script(w) : P2Script(w);

        [Test, Explicit("需要本机在跑 go cmd/server + go cmd/client；见类注释")]
        public void CSharpClient_SyncsWithGoBot_OverRealServer()
        {
            string server = Environment.GetEnvironmentVariable("FTG_SERVER") ?? "127.0.0.1:7777";
            int frames = int.TryParse(Environment.GetEnvironmentVariable("FTG_FRAMES"), out int fv) ? fv : 180;

            int split = server.LastIndexOf(':');
            string host = server.Substring(0, split);
            int port = int.Parse(server.Substring(split + 1));

            var join = new Proto.JoinRequest { MatchId = "m1", CharacterId = "Frank", ProtocolVersion = 1 };
            var ct = new NetClientTransport(host, port, join, 32);
            try
            {
                if (!ct.WaitReady(TimeSpan.FromSeconds(3)))
                {
                    Assert.Ignore($"未在 3s 内与 {server} 的对家握手成功。" +
                        "请先运行 go run ./cmd/server 与 go run ./cmd/client，再单独跑本测试。");
                }

                int seat = ct.Seat;
                Assert.That(seat == 1 || seat == 2, Is.True, $"座位分配异常：{seat}");

                // 配置取【服务器权威】开局头——两语言客户端拿到同一份 MatchSetup，规则天然一致。
                BattleConfig config = ReplayProtoCodec.FromProto(ct.Setup.Config);

                BattleSimulation sim = BuildNetworkSim(config);
                var driver = new RollbackDriver(sim, ct,
                    w => { var (d, h) = ScriptForSeat(seat, w); return new LocalInput(d, h); },
                    localIsP1: seat == 1);

                // 约 60Hz 驱动，直到 confirmed 追到目标帧（confirmed 的推进速率受对家喂输入的速率约束）。
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (driver.ConfirmedFrame < frames && DateTime.UtcNow < deadline)
                {
                    driver.Advance();
                    Thread.Sleep(16);
                }

                Assert.That(driver.ConfirmedFrame, Is.GreaterThanOrEqualTo(frames),
                    $"未在超时内确认到 {frames} 帧（confirmed={driver.ConfirmedFrame}）——" +
                    "对家可能没在跑或帧数不足（把 go cmd/client 的 -frames 调大到 ≥ 本测试帧数）。");

                // 独立参照：同配置、同两条脚本，单机跑 N 帧的确定性哈希轨迹（与 live 走完全不同的代码路径）。
                List<ulong> reference = ReferenceTrace(config, frames);

                IReadOnlyList<ulong> live = driver.ConfirmedTrace;
                for (int i = 0; i < frames; i++)
                    Assert.That(live[i], Is.EqualTo(reference[i]),
                        $"帧 {i + 1} 哈希与单机参照分歧（跨语言 desync）：live={live[i]:x16} ref={reference[i]:x16}");

                UnityEngine.Debug.Log(
                    $"[跨语言实机对拍] 本端座位 P{seat}，{frames} 帧 confirmed 逐位等于单机参照；" +
                    $"末帧哈希 {live[frames - 1]:x16}（应与 Go cmd/client 的末帧哈希一致）。" +
                    $"回滚修正 {driver.Corrections}，最大回滚 {driver.MaxRollback} 帧。");
            }
            finally
            {
                ct.Dispose();
            }
        }

        // ---- 装配（headless，与 RollbackDriverTests 一致）----

        private static FighterDefinition Frank() => TestBattleFactory.CreateRepository().Get("Frank");

        private static BattleSimulation BuildNetworkSim(BattleConfig config)
        {
            FighterDefinition def = Frank();
            FighterState p1 = BuildFighter("P1", -1f, new NetworkSeat(), def);
            FighterState p2 = BuildFighter("P2", 1f, new NetworkSeat(), def);
            return new BattleSimulation(p1, p2, new CollisionResolver(), config);
        }

        private static List<ulong> ReferenceTrace(BattleConfig config, int n)
        {
            FighterDefinition def = Frank();
            FighterState p1 = BuildFighter("P1", -1f,
                new ScriptedSeat(w => { var (d, h) = P1Script(w); return new ScriptedInput(d, h); }), def);
            FighterState p2 = BuildFighter("P2", 1f,
                new ScriptedSeat(w => { var (d, h) = P2Script(w); return new ScriptedInput(d, h); }), def);
            var sim = new BattleSimulation(p1, p2, new CollisionResolver(), config);

            var trace = new List<ulong>(n);
            for (int i = 1; i <= n; i++)
            {
                sim.Tick();
                trace.Add(StateHasher.HashState(sim));
            }
            return trace;
        }

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
    }
}
