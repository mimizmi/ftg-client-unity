using System.IO;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using Domain.Net;
using Google.Protobuf;
using UnityEditor;
using UnityEngine;
using Proto = FTG.Net.Proto;

namespace FTG.Tests
{
    /// <summary>
    /// 跨语言对拍夹具导出（N4 收官）。写两份 protobuf 到 server/testdata：
    ///   · duel_replay.pb  —— 一段确定输入流（= 对拍的输入夹具）
    ///   · duel_hashlog.pb —— 同一输入喂 C# BattleSimulation 逐帧 StateHasher 的规范哈希
    /// Go 侧 sim/duel 用【同一份】duel_replay.pb 驱动自己的模拟，逐帧与 duel_hashlog.pb 比对——
    /// 首个分歧帧即定点/移植 bug 的落点。
    ///
    /// 【何时重导】改过任何影响模拟的东西（招式数值、判定框 JSON、状态机逻辑）后。
    /// 导出用 TestBattleFactory.BuildWithSeats（= BattleBootstrap.BuildPlayer 的 headless 复刻，
    /// 判定框从 Assets/BoxData/*.json 装载），与运行时装配逐字一致。
    ///
    /// 【输入唯一源】P1/P2 脚本是纯函数 (帧号 → 方向+按住)，同时喂给 ScriptedSeat（驱动模拟）
    /// 与 Replay 录制（pressed = held & ~prevHeld，与 ScriptedSeat 内部推导逐位一致）。
    /// </summary>
    public static class DuelFixtureExporter
    {
        private const int Frames = 180;               // 3 秒
        private const string CharacterId = "Frank";   // 镜像内战

        [MenuItem("FG/导出对拍夹具（Replay+HashLog）")]
        public static void Export()
        {
            BattleConfig config = new BattleConfig(); // 默认：99 秒、三局两胜、满血 1000
            var sim = TestBattleFactory.BuildWithSeats(
                new ScriptedSeat(P1Script), new ScriptedSeat(P2Script), config, CharacterId);

            Proto.MatchSetup setup = BuildSetup(config);
            var replay = new Proto.Replay { Setup = setup };
            var hashLog = new Proto.HashLog { Setup = setup };

            ButtonMask prev1 = ButtonMask.None, prev2 = ButtonMask.None;
            for (int f = 1; f <= Frames; f++)
            {
                // 录制本帧输入（pressed 边沿推导，与 ScriptedSeat 一致）
                ScriptedInput s1 = P1Script(f);
                ScriptedInput s2 = P2Script(f);
                replay.Frames.Add(new Proto.FrameInputs
                {
                    Frame = (uint)f,
                    P1 = ToProtoInput(s1, ref prev1),
                    P2 = ToProtoInput(s2, ref prev2),
                });

                // 推进一帧并记录规范哈希（StateHasher = 对拍契约，与 Go statehash 逐字节镜像）
                sim.Tick();
                hashLog.Hashes.Add(new Proto.FrameHash
                {
                    Frame = (uint)sim.CurrentFrame,
                    Hash = StateHasher.HashState(sim),
                });
            }

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "server", "testdata"));
            Directory.CreateDirectory(dir);
            string replayPath = Path.Combine(dir, "duel_replay.pb");
            string hashPath = Path.Combine(dir, "duel_hashlog.pb");
            File.WriteAllBytes(replayPath, replay.ToByteArray());
            File.WriteAllBytes(hashPath, hashLog.ToByteArray());

            Debug.Log($"[DuelFixtureExporter] 已导出 {Frames} 帧对拍夹具：\n{replayPath}\n{hashPath}\n" +
                      "请提交进 git；Go 侧 go test ./sim/duel/ 的跨语言对拍测试将自动启用。");
        }

        private static Proto.MatchSetup BuildSetup(BattleConfig config) => new Proto.MatchSetup
        {
            P1CharacterId = CharacterId,
            P2CharacterId = CharacterId,
            ProtocolVersion = 1,
            Config = new Proto.BattleConfig
            {
                RoundFrames = config.RoundFrames,
                IntroFrames = config.IntroFrames,
                RoundOverFrames = config.RoundOverFrames,
                RoundsToWin = config.RoundsToWin,
                MaxHealth = config.MaxHealth,
            },
        };

        private static Proto.Input ToProtoInput(ScriptedInput s, ref ButtonMask prevHeld)
        {
            ButtonMask pressed = s.Held & ~prevHeld; // 与 ScriptedSeat.ManualTick 逐位一致
            prevHeld = s.Held;
            return new Proto.Input
            {
                Direction = s.Direction,
                Held = (uint)s.Held,
                Pressed = (uint)pressed,
            };
        }

        // ---- 输入脚本（纯函数，唯一真源）----
        // P1 从左侧走近后连点 LP；P2 从右侧走近、中途蹲、也连点 LP。走位/推挡/朝向翻面/
        // 出招/受击/顿帧全被这段覆盖，是判定链的密集采样。

        private static ScriptedInput P1Script(int f)
        {
            if (f <= 30) return new ScriptedInput(6, ButtonMask.None);       // 前进逼近
            if (f % 6 == 0) return new ScriptedInput(5, ButtonMask.LP);      // 连点 LP
            return new ScriptedInput(5, ButtonMask.None);
        }

        private static ScriptedInput P2Script(int f)
        {
            if (f <= 30) return new ScriptedInput(4, ButtonMask.None);       // 世界左 = 朝 P1 逼近
            if (f >= 40 && f <= 55) return new ScriptedInput(2, ButtonMask.None); // 蹲
            if (f % 7 == 0) return new ScriptedInput(5, ButtonMask.LP);      // 连点 LP
            return new ScriptedInput(5, ButtonMask.None);
        }
    }
}
