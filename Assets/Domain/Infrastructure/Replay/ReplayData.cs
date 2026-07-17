using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Replay
{
    /// <summary>
    /// 一帧的双方输入。只存"模拟消费了什么"：方向 + Held + Pressed。
    /// Pressed 必须单独存——事件锁存（FTGInputSO.latchedPresses）会让 Pressed
    /// 包含无法从 Held 序列推导的短按位；Released 则可由 Held 序列重建，不存。
    /// 每帧 6 字节：60Hz 下一分钟 21.6KB，一整场比赛不过几百 KB。
    /// </summary>
    public struct ReplayInputFrame
    {
        public byte P1Direction;
        public ButtonMask P1Held;
        public ButtonMask P1Pressed;
        public byte P2Direction;
        public ButtonMask P2Held;
        public ButtonMask P2Pressed;
    }

    /// <summary>
    /// 一场比赛的完整回放：对阵信息 + 回合规则 + 逐帧输入流。
    /// 确定性模拟保证：同样的数据喂回去 = 逐比特复现整场比赛。
    /// 这也是回滚网络的数据原型——回滚就是"从快照起点重放最近 N 帧输入"。
    /// </summary>
    public sealed class ReplayData
    {
        public const ushort CurrentVersion = 1;

        public ushort Version = CurrentVersion;
        public string P1CharacterId;
        public string P2CharacterId;

        /// <summary>录制时的回合规则。回放必须用它而非场景当前配置，否则时间线对不上。</summary>
        public BattleConfig Config;

        public List<ReplayInputFrame> Frames = new List<ReplayInputFrame>();

        public int FrameCount => Frames.Count;
    }
}
