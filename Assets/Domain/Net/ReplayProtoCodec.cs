using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Replay;
using Google.Protobuf;
using Proto = FTG.Net.Proto;

namespace Domain.Net
{
    /// <summary>
    /// 回放 ↔ protobuf 适配：把 FTGR 二进制回放的等价数据搬进 <c>ftg.v1.Replay</c>。
    /// 这是 protobuf 的第二序列化通道，也是 N4 跨语言对拍的【输入夹具】导出/导入口——
    /// 同一个 <see cref="Proto.Replay"/> 喂给 C# 与 Go 两套确定性模拟，各自吐哈希再比对。
    ///
    /// 边界纪律：这里只搬【输入流 + 规则头】，不含任何 float——ButtonMask/方向是整数位，
    /// 定点状态另走 <see cref="SnapshotProtoCodec"/>。帧号 1 起，与 ReplaySeat.CurrentFrame 对齐。
    /// </summary>
    public static class ReplayProtoCodec
    {
        // ---- 序列化收口（测试与网络层只碰 byte[] / ReplayData，不直接触 protobuf 类型）----

        public static byte[] ToBytes(ReplayData data) => ToProto(data).ToByteArray();

        public static ReplayData FromBytes(byte[] bytes) => FromProto(Proto.Replay.Parser.ParseFrom(bytes));

        // ---- ReplayData ↔ Proto.Replay ----

        public static Proto.Replay ToProto(ReplayData data)
        {
            var replay = new Proto.Replay
            {
                Setup = new Proto.MatchSetup
                {
                    P1CharacterId = data.P1CharacterId ?? "",
                    P2CharacterId = data.P2CharacterId ?? "",
                    ProtocolVersion = data.Version,
                    Config = ToProto(data.Config ?? new BattleConfig()),
                },
            };

            for (int i = 0; i < data.Frames.Count; i++)
            {
                ReplayInputFrame f = data.Frames[i];
                replay.Frames.Add(new Proto.FrameInputs
                {
                    Frame = (uint)(i + 1), // 1 起，对齐 ReplaySeat 的 CurrentFrame
                    P1 = new Proto.Input { Direction = f.P1Direction, Held = (uint)f.P1Held, Pressed = (uint)f.P1Pressed },
                    P2 = new Proto.Input { Direction = f.P2Direction, Held = (uint)f.P2Held, Pressed = (uint)f.P2Pressed },
                });
            }
            return replay;
        }

        public static ReplayData FromProto(Proto.Replay replay)
        {
            Proto.MatchSetup setup = replay.Setup ?? new Proto.MatchSetup();
            var data = new ReplayData
            {
                Version = (ushort)setup.ProtocolVersion,
                P1CharacterId = setup.P1CharacterId,
                P2CharacterId = setup.P2CharacterId,
                Config = FromProto(setup.Config ?? new Proto.BattleConfig()),
            };

            data.Frames.Capacity = replay.Frames.Count;
            foreach (Proto.FrameInputs fi in replay.Frames)
            {
                Proto.Input p1 = fi.P1 ?? new Proto.Input();
                Proto.Input p2 = fi.P2 ?? new Proto.Input();
                data.Frames.Add(new ReplayInputFrame
                {
                    P1Direction = (byte)p1.Direction,
                    P1Held = (ButtonMask)(byte)p1.Held,
                    P1Pressed = (ButtonMask)(byte)p1.Pressed,
                    P2Direction = (byte)p2.Direction,
                    P2Held = (ButtonMask)(byte)p2.Held,
                    P2Pressed = (ButtonMask)(byte)p2.Pressed,
                });
            }
            return data;
        }

        // ---- BattleConfig ↔ Proto.BattleConfig ----

        public static Proto.BattleConfig ToProto(BattleConfig c) => new Proto.BattleConfig
        {
            RoundFrames = c.RoundFrames,
            IntroFrames = c.IntroFrames,
            RoundOverFrames = c.RoundOverFrames,
            RoundsToWin = c.RoundsToWin,
            MaxHealth = c.MaxHealth,
        };

        public static BattleConfig FromProto(Proto.BattleConfig c) => new BattleConfig
        {
            RoundFrames = c.RoundFrames,
            IntroFrames = c.IntroFrames,
            RoundOverFrames = c.RoundOverFrames,
            RoundsToWin = c.RoundsToWin,
            MaxHealth = c.MaxHealth,
        };
    }
}
