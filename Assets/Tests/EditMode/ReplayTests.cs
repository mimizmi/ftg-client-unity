using System.Collections.Generic;
using System.IO;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Replay;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 回放系统的核心承诺：录下的输入流喂回去 = 逐帧复现整场比赛。
    /// 这条测试同时覆盖 录制 → 二进制序列化往返 → ReplaySeat 重播 全链路，
    /// 也是 Phase 4 回滚（= 实时局部回放）的可行性证明。
    /// </summary>
    public class ReplayTests
    {
        private const int Frames = 600;

        private static ScriptedInput P1Script(int frame)
        {
            if (frame <= 90) return new ScriptedInput(6);
            return frame % 12 == 0
                ? new ScriptedInput(5, ButtonMask.LP)
                : new ScriptedInput(5);
        }

        private static ScriptedInput P2Script(int frame)
        {
            if (frame <= 90) return new ScriptedInput(4);
            if (frame > 200 && frame <= 320 && frame % 20 == 0)
                return new ScriptedInput(2, ButtonMask.LK);
            if (frame > 200 && frame <= 320) return new ScriptedInput(2);
            return new ScriptedInput(5);
        }

        [Test]
        public void Replay_RoundTrip_ReproducesBattleFrameByFrame()
        {
            // ① 原始对局 + 录制
            var config = new BattleConfig { RoundsToWin = 1, RoundOverFrames = 30 };
            BattleSimulation original = TestBattleFactory.Build(P1Script, P2Script, config);
            var recorder = new ReplayRecorder(original, "Frank", "Frank");

            var originalHashes = new List<ulong>(Frames);
            for (int i = 0; i < Frames; i++)
            {
                original.Tick();
                originalHashes.Add(TestBattleFactory.HashState(original));
            }
            recorder.Stop();
            Assert.That(recorder.Data.FrameCount, Is.EqualTo(Frames), "每逻辑帧应录一条");

            // ② 二进制序列化往返
            var stream = new MemoryStream();
            ReplayIO.Save(recorder.Data, stream);
            stream.Position = 0;
            ReplayData loaded = ReplayIO.Load(stream);

            Assert.That(loaded.FrameCount, Is.EqualTo(Frames));
            Assert.That(loaded.P1CharacterId, Is.EqualTo("Frank"));
            Assert.That(loaded.Config.RoundsToWin, Is.EqualTo(1));
            Assert.That(loaded.Config.RoundOverFrames, Is.EqualTo(30));

            // ③ 用反序列化的数据重播，逐帧对表
            BattleSimulation replayed = TestBattleFactory.BuildWithSeats(
                new ReplaySeat(loaded, isP1: true),
                new ReplaySeat(loaded, isP1: false),
                loaded.Config);

            for (int i = 0; i < Frames; i++)
            {
                replayed.Tick();
                ulong hash = TestBattleFactory.HashState(replayed);
                if (hash != originalHashes[i])
                    Assert.Fail($"回放在第 {i + 1} 帧偏离原始对局：" +
                                $"{hash:X16} ≠ {originalHashes[i]:X16}");
            }
        }

        [Test]
        public void Replay_ExhaustedSeat_FeedsNeutral()
        {
            var data = new ReplayData
            {
                P1CharacterId = "Frank",
                P2CharacterId = "Frank",
                Config = new BattleConfig(),
            };
            data.Frames.Add(new ReplayInputFrame
            {
                P1Direction = 6, P1Held = ButtonMask.LP, P1Pressed = ButtonMask.LP,
                P2Direction = 5,
            });

            var seat = new ReplaySeat(data, isP1: true);
            seat.ManualTick();
            Assert.That(seat.Buffer.Latest.Direction, Is.EqualTo(6));
            Assert.That(seat.Exhausted, Is.True);

            seat.ManualTick(); // 帧耗尽 → 空闲输入，且松开的键要有 Released 边沿
            Assert.That(seat.Buffer.Latest.Direction, Is.EqualTo(5));
            Assert.That(seat.Buffer.Latest.Held, Is.EqualTo(ButtonMask.None));
            Assert.That(seat.Buffer.Latest.Released, Is.EqualTo(ButtonMask.LP));
        }
    }
}
