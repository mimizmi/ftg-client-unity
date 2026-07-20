using Domain.Infrastructure.Input;

namespace Domain.Net.Transport
{
    /// <summary>
    /// 过线的最小单元：某【绝对帧号】的一个玩家输入。逐字保真 Direction/Held/Pressed
    /// （Pressed 含无法从 Held 推导的单帧点按位），是回滚“输入即状态”纪律的搬运载体。
    /// 与 Go 侧 lockstep.InputPacket 逐字段对应，跨语言线格式见 proto/ftg/v1/net.proto。
    /// </summary>
    public struct InputPacket
    {
        public int Frame;
        public InputFrame Input;

        public InputPacket(int frame, in InputFrame input)
        {
            Frame = frame;
            Input = input;
        }
    }

    /// <summary>
    /// 一端看到的连接质量快照（不依赖墙钟，可确定性测试）。RttFrames 是帧为单位的往返延迟估计：
    /// 本端帧 F 单程 L 步到对端、对端 ack 再走 L 步回来，故 PeerAckFrame≈F−2L，RttFrames≈2L。
    /// 供 UI 显示、延迟自适应、掉线判定。镜像 Go 侧 lockstep.ConnStats。
    /// </summary>
    public struct ConnStats
    {
        public int LocalFrame;   // 最新本地输入帧
        public int PeerAckFrame; // 对端已确认收到的本端最高帧
        public int RemoteFrame;  // 已连续收到的远端最高帧
        public int RttFrames;    // 往返延迟估计（帧）= LocalFrame − PeerAckFrame
        public int StaleSteps;   // 连续无新远端帧的本地步数（越大越可能掉线）
    }

    /// <summary>
    /// 连接的宏观状态，由“远端新鲜度”StaleSteps 阈值判定（不依赖墙钟）。镜像 Go 侧 ConnectionState。
    /// </summary>
    public enum ConnectionState
    {
        Connecting,   // 握手中（尚未双方到齐）
        Connected,    // 正常：近期有新远端输入
        Stalled,      // 短暂断流：超警戒阈值，仍在重连尝试
        Disconnected, // 长时间断流：超掉线阈值，视为掉线
    }
}
