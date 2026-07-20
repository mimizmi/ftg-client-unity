using System.Collections.Generic;

namespace Domain.Net.Transport
{
    /// <summary>
    /// 进程内单向信道，latency = 整数帧延迟（0=同帧到达）。Step 推进一格逻辑时间，把到期包放入可读队列。
    /// 成对使用即全双工链路（见 <see cref="LoopbackLink.NewPair"/>）。镜像 Go 侧 lockstep.Pipe，
    /// 供 EditMode 回滚双模拟测试注入可控延迟（无需真 socket）。
    /// </summary>
    public sealed class LoopbackPipe
    {
        private readonly int latency;
        private int clock;
        private readonly List<Timed> inFlight = new List<Timed>();
        private readonly List<InputPacket> ready = new List<InputPacket>();

        private struct Timed
        {
            public int ArriveAt;
            public InputPacket Packet;
        }

        public LoopbackPipe(int latency) { this.latency = latency; }

        /// <summary>压入在途队列，到达时刻 = 当前时钟 + latency。</summary>
        public void Send(in InputPacket p) => inFlight.Add(new Timed { ArriveAt = clock + latency, Packet = p });

        /// <summary>推进一格逻辑时间：把到期的在途包转入可读队列（保持发送先后次序）。</summary>
        public void Step()
        {
            clock++;
            for (int i = 0; i < inFlight.Count; i++)
            {
                if (inFlight[i].ArriveAt <= clock)
                {
                    ready.Add(inFlight[i].Packet);
                    inFlight.RemoveAt(i);
                    i--;
                }
            }
        }

        /// <summary>取走并清空当前可读队列。</summary>
        public List<InputPacket> Drain()
        {
            if (ready.Count == 0) return new List<InputPacket>();
            var outp = new List<InputPacket>(ready);
            ready.Clear();
            return outp;
        }
    }

    /// <summary>
    /// 把“发往对端的 Pipe”与“从对端收的 Pipe”绑成一个 <see cref="ITransport"/> 视图。镜像 Go 侧 endpoint。
    /// </summary>
    public sealed class LoopbackLink : ITransport
    {
        private readonly LoopbackPipe outPipe;
        private readonly LoopbackPipe inPipe;

        private LoopbackLink(LoopbackPipe outPipe, LoopbackPipe inPipe)
        {
            this.outPipe = outPipe;
            this.inPipe = inPipe;
        }

        public void Send(in InputPacket p) => outPipe.Send(p);
        public List<InputPacket> Drain() => inPipe.Drain();

        /// <summary>
        /// 造一条全双工链路：返回 A、B 两端 <see cref="ITransport"/> 与两条底层 Pipe（测试驱动时钟用）。
        /// latency 为单程整数帧延迟，两向对称。每逻辑步对两条 Pipe 各 Step 一次即模拟时间流逝。
        /// </summary>
        public static (LoopbackLink a, LoopbackLink b, LoopbackPipe aToB, LoopbackPipe bToA) NewPair(int latency)
        {
            var aToB = new LoopbackPipe(latency);
            var bToA = new LoopbackPipe(latency);
            return (new LoopbackLink(aToB, bToA), new LoopbackLink(bToA, aToB), aToB, bToA);
        }
    }
}
