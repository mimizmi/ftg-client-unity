using System.Collections.Generic;
using Domain.Infrastructure.Input;
using Domain.Net.Transport;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// Windower 的纯逻辑单测——不碰 socket，验证“冗余窗口 + 去重 + ack 裁剪 + 连接统计”的四条不变量，
    /// 与 Go 侧 lockstep 的 windower/robust 测试对称（同一算法两语言各钉一遍）。
    /// 这是 C# 传输层（P1）的正确性护栏：回滚驱动接上来之前，先证明簿记本身无误。
    /// </summary>
    public class WindowerTests
    {
        private static InputPacket Pkt(int frame, byte dir = 5) =>
            new InputPacket(frame, new InputFrame { Frame = frame, Direction = dir });

        // 把某端 Local 出的冗余窗口喂给对端 Remote，返回对端本次吐出的【新】帧号。
        private static List<int> DeliverFrames(Windower to, IReadOnlyList<InputPacket> win)
        {
            var fresh = to.Remote(win);
            var ids = new List<int>(fresh.Count);
            foreach (var p in fresh) ids.Add(p.Frame);
            return ids;
        }

        [Test]
        public void Remote_DedupsAndAdvancesContiguousAck()
        {
            var send = new Windower(8);
            var recv = new Windower(8);

            // 帧 1、2 各发一次冗余窗口（含历史）：接收端每帧只应吐一个【新】帧，ack 连续推进。
            Assert.AreEqual(new List<int> { 1 }, DeliverFrames(recv, send.Local(Pkt(1))));
            Assert.AreEqual(1, recv.Ack());

            Assert.AreEqual(new List<int> { 2 }, DeliverFrames(recv, send.Local(Pkt(2))));
            Assert.AreEqual(2, recv.Ack());

            // 把帧 2 的窗口（含帧 1、2）再投一次：全是旧帧，去重后无新帧、ack 不动。
            var replay = new List<InputPacket> { Pkt(1), Pkt(2) };
            Assert.IsEmpty(DeliverFrames(recv, replay));
            Assert.AreEqual(2, recv.Ack());
        }

        [Test]
        public void RedundantWindow_RecoversSingleLoss()
        {
            var send = new Windower(8);
            var recv = new Windower(8);

            // 帧 1 送达。
            DeliverFrames(recv, send.Local(Pkt(1)));
            // 帧 2 的数据报“丢了”——不投递，但 send 端已记入历史。
            var _ = send.Local(Pkt(2));
            // 帧 3 的冗余窗口携带 [1,2,3]：帧 2 被补回，ack 直接跳到 3（无需重传）。
            var fresh = DeliverFrames(recv, send.Local(Pkt(3)));
            CollectionAssert.AreEquivalent(new List<int> { 2, 3 }, fresh);
            Assert.AreEqual(3, recv.Ack());
        }

        [Test]
        public void GapBlocksAck_UntilFilled()
        {
            var send = new Windower(8);
            var recv = new Windower(8);

            DeliverFrames(recv, send.Local(Pkt(1)));
            send.Local(Pkt(2)); // 记帧 2
            send.Local(Pkt(3)); // 记帧 3

            // 先只投递帧 3 那一“帧”单独的包（模拟乱序：只把最新一帧单发，绕过冗余）。
            DeliverFrames(recv, new List<InputPacket> { Pkt(3) });
            Assert.AreEqual(1, recv.Ack(), "帧 2 缺失，ack 应卡在 1");

            // 帧 2 补到：ack 连续推进到 3。
            DeliverFrames(recv, new List<InputPacket> { Pkt(2) });
            Assert.AreEqual(3, recv.Ack());
        }

        [Test]
        public void Ack_TrimsResendWindow_ButAlwaysKeepsLatest()
        {
            var send = new Windower(32);

            // 发到帧 5，未收任何 ack：窗口应含 1..5 全部（冗余重发）。
            List<InputPacket> win = null;
            for (int f = 1; f <= 5; f++) win = send.Local(Pkt(f));
            Assert.AreEqual(5, win.Count, "无 ack 时应冗余重发全部在窗历史");
            Assert.AreEqual(1, win[0].Frame);
            Assert.AreEqual(5, win[win.Count - 1].Frame);

            // 对端确认已连续收到帧 4：下一次发帧 6，窗口只留 > 4 的帧（5、6）。
            send.RecordPeerAck(4);
            win = send.Local(Pkt(6));
            CollectionAssert.AreEqual(new List<int> { 5, 6 }, FramesOf(win));

            // 对端 ack 追到最新帧 6：仍保底发最新一帧（空窗口会卡死对端）。
            send.RecordPeerAck(6);
            win = send.Local(Pkt(7));
            CollectionAssert.AreEqual(new List<int> { 7 }, FramesOf(win));
        }

        [Test]
        public void Stats_TracksRttAndStaleness()
        {
            var send = new Windower(32);

            // 走到本地帧 10，对端已 ack 到 4：RTT≈本地−ack=6。
            for (int f = 1; f <= 10; f++) send.Local(Pkt(f));
            send.RecordPeerAck(4);
            var s = send.Stats();
            Assert.AreEqual(10, s.LocalFrame);
            Assert.AreEqual(4, s.PeerAckFrame);
            Assert.AreEqual(6, s.RttFrames);

            // 连走 2 个本地步都没收到新远端帧：StaleSteps 累加（断线信号）。
            send.Local(Pkt(11));
            send.Local(Pkt(12));
            Assert.GreaterOrEqual(send.Stats().StaleSteps, 2);

            // 收到一个新远端帧：新鲜度清零。
            send.Remote(new List<InputPacket> { Pkt(1) });
            Assert.AreEqual(0, send.Stats().StaleSteps);
        }

        [Test]
        public void ClassifyState_MatchesThresholds()
        {
            const int warn = 30, dead = 180;
            Assert.AreEqual(ConnectionState.Connecting, NetClientTransport.ClassifyState(false, 0, warn, dead));
            Assert.AreEqual(ConnectionState.Connecting, NetClientTransport.ClassifyState(false, 999, warn, dead));
            Assert.AreEqual(ConnectionState.Connected, NetClientTransport.ClassifyState(true, 0, warn, dead));
            Assert.AreEqual(ConnectionState.Connected, NetClientTransport.ClassifyState(true, 30, warn, dead)); // ==warn 仍算已连接
            Assert.AreEqual(ConnectionState.Stalled, NetClientTransport.ClassifyState(true, 31, warn, dead));
            Assert.AreEqual(ConnectionState.Stalled, NetClientTransport.ClassifyState(true, 180, warn, dead));
            Assert.AreEqual(ConnectionState.Disconnected, NetClientTransport.ClassifyState(true, 181, warn, dead));
        }

        private static List<int> FramesOf(IReadOnlyList<InputPacket> win)
        {
            var ids = new List<int>(win.Count);
            foreach (var p in win) ids.Add(p.Frame);
            return ids;
        }
    }
}
