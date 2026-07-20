using System.Collections.Generic;

namespace Domain.Net.Transport
{
    /// <summary>
    /// UDP 抗丢包的核心簿记：发送端把每帧的本地输入累积成【冗余窗口】（最近 W 帧），
    /// 接收端按帧号【去重】并追踪已连续收到的最高帧（ack）。单个数据报丢失，只要后续 W 个
    /// 数据报里有一个到达，被丢的帧就被补回——无需重传。
    ///
    /// 这是 Go 侧 lockstep.Windower 的逐字 C# 移植：同一套“冗余+去重+ack 裁剪”逻辑，
    /// Go 已被确定性丢包测试反复验证，此处由 <see cref="NetClientTransport"/> 复用。
    ///
    /// 非并发安全：调用方（NetClientTransport 在其锁下）负责同步。
    /// </summary>
    public sealed class Windower
    {
        private readonly int windowSize;
        private readonly List<InputPacket> outHistory = new List<InputPacket>(); // 本端已发帧，尾部保留最近 windowSize 个
        private readonly HashSet<int> seen = new HashSet<int>();                 // 远端帧去重
        private int contig;  // 已连续收到的远端最高帧
        private int peerAck; // 对端已连续收到的【本端】最高帧（从收到的报文里学得），用于裁剪重发窗口

        private int lastLocalFrame; // 最新本地帧号
        private int staleSteps;     // 连续多少个本地步没收到【新】远端帧（断线信号）

        /// <summary>windowSize ≤ 0 取默认 32。</summary>
        public Windower(int windowSize = 32)
        {
            this.windowSize = windowSize <= 0 ? 32 : windowSize;
        }

        /// <summary>
        /// 记录一帧本地输入，返回本次应发送的冗余窗口。窗口 = outHistory 中【对端尚未确认】的帧
        /// （Frame &gt; peerAck），上限 windowSize 帧。已被 ack 的帧不再重发（省带宽）；但至少发最新一帧
        /// （空窗口会令对端永远收不到新输入而卡死）。peerAck 陈旧只会多发几帧，绝不少发，故安全。
        /// </summary>
        public List<InputPacket> Local(in InputPacket p)
        {
            lastLocalFrame = p.Frame;
            staleSteps++; // 本地走一步；若本步收到新远端帧，Remote 会清零
            outHistory.Add(p);
            if (outHistory.Count > windowSize)
                outHistory.RemoveRange(0, outHistory.Count - windowSize);

            // 跳过已确认帧，但保底留下最新一帧。
            int start = 0;
            while (start < outHistory.Count - 1 && outHistory[start].Frame <= peerAck)
                start++;

            var win = new List<InputPacket>(outHistory.Count - start);
            for (int i = start; i < outHistory.Count; i++)
                win.Add(outHistory[i]);
            return win;
        }

        /// <summary>记录对端已连续收到的本端最高帧（单调不减），据此裁剪后续重发窗口。</summary>
        public void RecordPeerAck(int ack)
        {
            if (ack > peerAck) peerAck = ack;
        }

        /// <summary>
        /// 吞入一个收到的冗余窗口，返回其中【首次见到】的帧（已按帧号去重）；顺带推进 ack。
        /// </summary>
        public List<InputPacket> Remote(IReadOnlyList<InputPacket> win)
        {
            var fresh = new List<InputPacket>();
            for (int i = 0; i < win.Count; i++)
            {
                var p = win[i];
                if (!seen.Add(p.Frame)) continue; // Add 返回 false = 已见过
                fresh.Add(p);
            }
            while (seen.Contains(contig + 1)) contig++;
            if (fresh.Count > 0) staleSteps = 0; // 收到新远端帧：连接新鲜
            return fresh;
        }

        /// <summary>已连续收到的远端最高帧号（供发送端裁剪窗口 / 拥塞判断）。</summary>
        public int Ack() => contig;

        /// <summary>当前连接质量快照。</summary>
        public ConnStats Stats() => new ConnStats
        {
            LocalFrame = lastLocalFrame,
            PeerAckFrame = peerAck,
            RemoteFrame = contig,
            RttFrames = lastLocalFrame - peerAck,
            StaleSteps = staleSteps,
        };
    }
}
