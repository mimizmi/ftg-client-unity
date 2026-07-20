using System.Collections.Generic;

namespace Domain.Net.Transport
{
    /// <summary>
    /// <see cref="ITransport"/> 装饰器：把【收到的】远端输入额外延迟 delayFrames 个 Drain 周期再放行，
    /// 在近零延迟的 localhost 上人工制造网络延迟——于是回滚的预测/回退真的会触发，【且仍收敛到同一
    /// confirmed 轨迹】。只延迟收、不延迟发（本端输入照常即时发出，不饿着对家）。纯客户端、不碰服务器。
    ///
    /// 用途：演示/测试「回滚在真网络下确实在回滚」。延迟以【逻辑帧】计（非墙钟），故触发的回滚窗口
    /// 稳定 ≈ delayFrames，断言不 flaky。生产联机的真实延迟来自网络本身，无需此装饰器。
    /// </summary>
    public sealed class DelayingTransport : ITransport
    {
        private readonly ITransport inner;
        private readonly int delayFrames;
        private int drainTick;
        private readonly List<Held> held = new List<Held>();

        private struct Held
        {
            public int ReleaseAt;
            public InputPacket Packet;
        }

        public DelayingTransport(ITransport inner, int delayFrames)
        {
            this.inner = inner;
            this.delayFrames = delayFrames < 0 ? 0 : delayFrames;
        }

        public void Send(in InputPacket p) => inner.Send(p);

        public List<InputPacket> Drain()
        {
            drainTick++;
            foreach (InputPacket p in inner.Drain())
                held.Add(new Held { ReleaseAt = drainTick + delayFrames, Packet = p });

            var ready = new List<InputPacket>();
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (held[i].ReleaseAt <= drainTick)
                {
                    ready.Add(held[i].Packet);
                    held.RemoveAt(i);
                }
            }
            return ready;
        }
    }
}
