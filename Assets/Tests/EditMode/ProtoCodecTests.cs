using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Replay;
using Domain.Net;
using Google.Protobuf;
using NUnit.Framework;
using Proto = FTG.Net.Proto;

namespace FTG.Tests
{
    /// <summary>
    /// N3 protobuf 协议的安全网：适配层把回放/状态搬进 protobuf 再搬回来，必须逐位无损、
    /// 且喂回确定性模拟仍逐帧复现原局。同时钉死两条跨语言契约——
    /// 规范对拍哈希 ≡ 确定性测试哈希、proto 枚举序数 ≡ C# byte 枚举序数。
    /// N4 引入 Go 侧真正对拍前，这些是协议正确性的第一道防线。
    /// </summary>
    public class ProtoCodecTests
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

        // ---- 1. protobuf 回放往返：序列化→反序列化→喂回模拟，逐帧哈希与原局完全一致 ----

        [Test]
        public void ReplayProto_RoundTrip_ReproducesBattleFrameByFrame()
        {
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
            Assert.That(recorder.Data.FrameCount, Is.EqualTo(Frames));

            // protobuf 二进制往返（对比 FTGR：这是第二序列化 + 对拍输入夹具格式）
            byte[] bytes = ReplayProtoCodec.ToBytes(recorder.Data);
            ReplayData loaded = ReplayProtoCodec.FromBytes(bytes);

            Assert.That(loaded.FrameCount, Is.EqualTo(Frames));
            Assert.That(loaded.P1CharacterId, Is.EqualTo("Frank"));
            Assert.That(loaded.Version, Is.EqualTo(ReplayData.CurrentVersion));
            Assert.That(loaded.Config.RoundsToWin, Is.EqualTo(1));
            Assert.That(loaded.Config.RoundOverFrames, Is.EqualTo(30));

            BattleSimulation replayed = TestBattleFactory.BuildWithSeats(
                new ReplaySeat(loaded, isP1: true),
                new ReplaySeat(loaded, isP1: false),
                loaded.Config);

            for (int i = 0; i < Frames; i++)
            {
                replayed.Tick();
                ulong hash = TestBattleFactory.HashState(replayed);
                if (hash != originalHashes[i])
                    Assert.Fail($"protobuf 回放在第 {i + 1} 帧偏离原始对局：" +
                                $"{hash:X16} ≠ {originalHashes[i]:X16}");
            }
        }

        // ---- 2. 规范对拍哈希 ≡ 确定性测试内联哈希（改一处即红）----

        [Test]
        public void StateHasher_MatchesDeterminismHash()
        {
            BattleSimulation sim = TestBattleFactory.Build(P1Script, P2Script);
            for (int i = 0; i < 120; i++) sim.Tick();

            Assert.That(StateHasher.HashState(sim), Is.EqualTo(TestBattleFactory.HashState(sim)),
                "StateHasher（协议契约）必须与确定性测试内联哈希逐位一致——Go 侧对拍镜像的就是它");
        }

        // ---- 3. 枚举序数守卫：proto 枚举 ≡ C# byte 枚举（改序不同步改 .proto 即红）----

        [Test]
        public void EnumOrdinals_AlignWithProto()
        {
            Assert.That((int)Proto.FighterStatus.Neutral, Is.EqualTo((int)FighterStatus.Neutral));
            Assert.That((int)Proto.FighterStatus.Attacking, Is.EqualTo((int)FighterStatus.Attacking));
            Assert.That((int)Proto.FighterStatus.CounterStance, Is.EqualTo((int)FighterStatus.CounterStance));
            Assert.That((int)Proto.FighterStatus.Hitstun, Is.EqualTo((int)FighterStatus.Hitstun));
            Assert.That((int)Proto.FighterStatus.Blockstun, Is.EqualTo((int)FighterStatus.Blockstun));

            Assert.That((int)Proto.MovementState.Idle, Is.EqualTo((int)MovementState.Idle));
            Assert.That((int)Proto.MovementState.CrouchEnter, Is.EqualTo((int)MovementState.CrouchEnter));
            Assert.That((int)Proto.MovementState.Crouch, Is.EqualTo((int)MovementState.Crouch));
            Assert.That((int)Proto.MovementState.CrouchExit, Is.EqualTo((int)MovementState.CrouchExit));
            Assert.That((int)Proto.MovementState.WalkForward, Is.EqualTo((int)MovementState.WalkForward));
            Assert.That((int)Proto.MovementState.WalkBackward, Is.EqualTo((int)MovementState.WalkBackward));
            Assert.That((int)Proto.MovementState.Dash, Is.EqualTo((int)MovementState.Dash));
            Assert.That((int)Proto.MovementState.Run, Is.EqualTo((int)MovementState.Run));
            Assert.That((int)Proto.MovementState.BackDash, Is.EqualTo((int)MovementState.BackDash));
            Assert.That((int)Proto.MovementState.Jumping, Is.EqualTo((int)MovementState.Jumping));
        }

        // ---- 4. FixVec2 定点向量逐位往返（含过线字节）----

        [Test]
        public void FixVec2_Proto_RoundTrip_BitExact()
        {
            var v = new FixVec2(Fix.FromRaw(-65536), Fix.FromRaw(32768)); // -1.0, 0.5

            Proto.FixVec2 p = SnapshotProtoCodec.ToProto(v);
            Assert.That(p.XRaw, Is.EqualTo(-65536));
            Assert.That(p.YRaw, Is.EqualTo(32768));

            FixVec2 back = SnapshotProtoCodec.FromProto(p);
            Assert.That(back.X.Raw, Is.EqualTo(v.X.Raw));
            Assert.That(back.Y.Raw, Is.EqualTo(v.Y.Raw));

            // 过 protobuf 线字节再解，Raw 仍逐位不变（sfixed32 契约）
            Proto.FixVec2 parsed = Proto.FixVec2.Parser.ParseFrom(p.ToByteArray());
            Assert.That(parsed.XRaw, Is.EqualTo(-65536));
            Assert.That(parsed.YRaw, Is.EqualTo(32768));
        }

        // ---- 5. BattleSnapshot 捕获确定性状态 + 哈希，过线不变 ----

        [Test]
        public void BattleSnapshot_CapturesDeterministicState()
        {
            BattleSimulation sim = TestBattleFactory.Build(P1Script, P2Script);
            for (int i = 0; i < 200; i++) sim.Tick();

            Proto.BattleSnapshot snap = SnapshotProtoCodec.ToProto(sim);
            Assert.That(snap.Frame, Is.EqualTo((uint)sim.CurrentFrame));
            Assert.That(snap.StateHash, Is.EqualTo(StateHasher.HashState(sim)));
            Assert.That(snap.P1.Position.XRaw, Is.EqualTo(sim.P1.Position.X.Raw));
            Assert.That(snap.P1.Position.YRaw, Is.EqualTo(sim.P1.Position.Y.Raw));
            Assert.That((int)snap.P1.Status, Is.EqualTo((int)sim.P1.Status));
            Assert.That(snap.P1.FacingRight, Is.EqualTo(sim.P1.FacingRight));
            Assert.That(snap.P1.CurrentMoveId, Is.EqualTo(sim.P1.CurrentMove?.MoveId ?? ""));

            // 过线：哈希与关键字段逐位保真
            Proto.BattleSnapshot parsed = Proto.BattleSnapshot.Parser.ParseFrom(snap.ToByteArray());
            Assert.That(parsed.StateHash, Is.EqualTo(snap.StateHash));
            Assert.That(parsed.P1.Position.XRaw, Is.EqualTo(snap.P1.Position.XRaw));
            Assert.That(parsed.P2.Position.YRaw, Is.EqualTo(snap.P2.Position.YRaw));
        }
    }
}
