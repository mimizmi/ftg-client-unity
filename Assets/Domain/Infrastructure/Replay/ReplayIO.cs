using System;
using System.IO;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Replay
{
    /// <summary>
    /// 回放的二进制序列化。格式（小端）：
    /// magic "FTGR" | ushort version | string p1Id | string p2Id
    /// | int×5 config(RoundFrames, IntroFrames, RoundOverFrames, RoundsToWin, MaxHealth)
    /// | int frameCount | frameCount × 6 字节（P1 方向/Held/Pressed，P2 同）。
    /// 版本不符直接拒载——输入语义变了的老档重放出来只会是另一场比赛。
    /// </summary>
    public static class ReplayIO
    {
        private const uint Magic = 0x52475446; // "FTGR" little-endian

        public static void Save(ReplayData data, Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(data.Version);
            writer.Write(data.P1CharacterId ?? "");
            writer.Write(data.P2CharacterId ?? "");

            BattleConfig c = data.Config ?? new BattleConfig();
            writer.Write(c.RoundFrames);
            writer.Write(c.IntroFrames);
            writer.Write(c.RoundOverFrames);
            writer.Write(c.RoundsToWin);
            writer.Write(c.MaxHealth);

            writer.Write(data.FrameCount);
            for (int i = 0; i < data.Frames.Count; i++)
            {
                ReplayInputFrame f = data.Frames[i];
                writer.Write(f.P1Direction);
                writer.Write((byte)f.P1Held);
                writer.Write((byte)f.P1Pressed);
                writer.Write(f.P2Direction);
                writer.Write((byte)f.P2Held);
                writer.Write((byte)f.P2Pressed);
            }
        }

        /// <summary>读取回放。格式/版本不符抛 InvalidDataException。</summary>
        public static ReplayData Load(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("不是 FTGR 回放文件");
            ushort version = reader.ReadUInt16();
            if (version != ReplayData.CurrentVersion)
                throw new InvalidDataException($"回放版本 {version} 与当前 {ReplayData.CurrentVersion} 不符");

            var data = new ReplayData
            {
                Version = version,
                P1CharacterId = reader.ReadString(),
                P2CharacterId = reader.ReadString(),
                Config = new BattleConfig
                {
                    RoundFrames = reader.ReadInt32(),
                    IntroFrames = reader.ReadInt32(),
                    RoundOverFrames = reader.ReadInt32(),
                    RoundsToWin = reader.ReadInt32(),
                    MaxHealth = reader.ReadInt32(),
                },
            };

            int count = reader.ReadInt32();
            data.Frames.Capacity = count;
            for (int i = 0; i < count; i++)
            {
                data.Frames.Add(new ReplayInputFrame
                {
                    P1Direction = reader.ReadByte(),
                    P1Held = (ButtonMask)reader.ReadByte(),
                    P1Pressed = (ButtonMask)reader.ReadByte(),
                    P2Direction = reader.ReadByte(),
                    P2Held = (ButtonMask)reader.ReadByte(),
                    P2Pressed = (ButtonMask)reader.ReadByte(),
                });
            }
            return data;
        }
    }
}
