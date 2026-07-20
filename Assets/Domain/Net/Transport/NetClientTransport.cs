using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Google.Protobuf;
using Proto = FTG.Net.Proto;

namespace Domain.Net.Transport
{
    /// <summary>
    /// 客户端侧的 <see cref="ITransport"/> 实现，把回滚驱动的 Send/Drain 落到真 UDP。
    /// 关键：回滚逻辑对它一无所知——同一套回滚代码，测试用假信道、线上用它，无缝替换。
    /// 这是 Go 侧 netcode.ClientTransport 的 C# 移植（逐方法对称）。
    ///
    /// 抗丢包：每次 Send 冗余携带最近 W 帧（单丢由下一报文补齐）；收端按 frame 去重，Drain 只吐【新】帧。
    /// 握手：向服务器发 JoinRequest，收 JoinResponse 得知座位与权威开局头，双方到齐即 Ready。
    /// 断线重连：凭稳定 ClientId 从新地址重发握手，服务器重映射回原座位。
    ///
    /// 线程模型：一个后台收包线程（读 UDP + 去重 + 心跳），主逻辑线程调 Send/Drain/Stats。
    /// 后台线程只碰 socket / protobuf / Windower（纯托管），绝不触 UnityEngine API，故 Unity 安全。
    /// </summary>
    public sealed class NetClientTransport : ITransport, IDisposable
    {
        private readonly UdpClient conn;
        private readonly Proto.JoinRequest join;
        private readonly int warnStale; // StaleSteps 超此 = 断流
        private readonly int deadStale; // StaleSteps 超此 = 掉线

        private readonly object gate = new object();
        private readonly Windower w;
        private readonly List<InputPacket> pending = new List<InputPacket>(); // 待 Drain 的新远端输入
        private int seat;
        private Proto.MatchSetup setup;
        private bool ready;

        private static readonly List<InputPacket> NoPackets = new List<InputPacket>(0);

        private volatile bool running = true;
        private readonly Thread recvThread;

        /// <summary>
        /// 连接服务器并启动收包线程。windowSize ≤ 0 取默认 32。join 无 ClientId 时自生成稳定 id（重连复用）。
        /// 构造后需调 <see cref="WaitReady"/> 等对局双方到齐，再开始 Send/Drain。
        /// </summary>
        public NetClientTransport(string host, int port, Proto.JoinRequest join, int windowSize = 32)
        {
            this.join = join ?? throw new ArgumentNullException(nameof(join));
            if (string.IsNullOrEmpty(this.join.ClientId))
                this.join.ClientId = NewClientId(); // 传输层自持稳定身份，重连用同一 id

            warnStale = 30;  // ~0.5s@60fps 无新远端输入 = 断流
            deadStale = 180; // ~3s 无新远端输入 = 掉线
            w = new Windower(windowSize);

            conn = new UdpClient();
            conn.Connect(host, port);
            conn.Client.ReceiveTimeout = 250; // ms：定期醒来做心跳/重连（无包到达时）

            recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "ftg-net-recv" };
            recvThread.Start();
        }

        // ---- 握手结果 ----

        /// <summary>本端座位：决定占 P1(1) 还是 P2(2)。</summary>
        public int Seat { get { lock (gate) return seat; } }

        /// <summary>服务器下发的权威开局头（角色/规则）。</summary>
        public Proto.MatchSetup Setup { get { lock (gate) return setup; } }

        /// <summary>双方是否已到齐可开打。</summary>
        public bool Ready { get { lock (gate) return ready; } }

        /// <summary>当前连接质量快照（RTT 帧数 / 远端新鲜度 / 断线信号）。</summary>
        public ConnStats Stats { get { lock (gate) return w.Stats(); } }

        /// <summary>当前连接宏观状态（连接中/已连接/断流/掉线）。</summary>
        public ConnectionState State
        {
            get { lock (gate) return ClassifyState(ready, w.Stats().StaleSteps, warnStale, deadStale); }
        }

        /// <summary>由是否就绪 + 新鲜度阈值判定连接状态（纯函数，便于单测）。镜像 Go 侧 classifyState。</summary>
        public static ConnectionState ClassifyState(bool ready, int stale, int warn, int dead)
        {
            if (!ready) return ConnectionState.Connecting;
            if (stale > dead) return ConnectionState.Disconnected;
            if (stale > warn) return ConnectionState.Stalled;
            return ConnectionState.Connected;
        }

        /// <summary>反复发握手直到服务器报双方到齐，或超时。UDP 不可靠，故靠重发拿到 ready。</summary>
        public bool WaitReady(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                SendJoin();
                for (int i = 0; i < 5; i++)
                {
                    if (Ready) return true;
                    Thread.Sleep(10);
                }
            }
            return false;
        }

        // ---- ITransport ----

        /// <summary>
        /// 记录本地输入并把冗余窗口（最近 W 帧）发给服务器（服务器转发对家）。窗口/ack 由共享 Windower 维护。
        /// </summary>
        public void Send(in InputPacket p)
        {
            List<InputPacket> win;
            int s, ack;
            lock (gate)
            {
                win = w.Local(p);
                s = seat;
                ack = w.Ack();
            }

            var dg = new Proto.InputDatagram { Seat = (uint)s, Ack = (uint)ack };
            for (int i = 0; i < win.Count; i++)
                dg.Inputs.Add(NetWire.ToNetInput(win[i]));
            Write(new Proto.Packet { Input = dg });
        }

        /// <summary>取走自上次以来新到的远端输入（已按 frame 去重）。空则返回共享空表（勿修改）。</summary>
        public List<InputPacket> Drain()
        {
            lock (gate)
            {
                if (pending.Count == 0) return NoPackets;
                var outp = new List<InputPacket>(pending);
                pending.Clear();
                return outp;
            }
        }

        // ---- 后台收包 + 心跳 ----

        private void RecvLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                byte[] data;
                try
                {
                    data = conn.Receive(ref remote);
                }
                catch (SocketException)
                {
                    // 收超时或对端瞬时不可达：借机做心跳——非「已连接」时重发握手，刷新服务器映射
                    // （换 socket / NAT 重绑 / 服务器重启后，凭稳定 ClientId 落回原座位）。
                    if (running && State != ConnectionState.Connected)
                        SendJoin();
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return; // Dispose 关了 socket
                }

                Proto.Packet pkt;
                try { pkt = Proto.Packet.Parser.ParseFrom(data); }
                catch { continue; } // 坏包丢弃

                switch (pkt.BodyCase)
                {
                    case Proto.Packet.BodyOneofCase.Joined:
                        OnJoined(pkt.Joined);
                        break;
                    case Proto.Packet.BodyOneofCase.Input:
                        OnInput(pkt.Input);
                        break;
                }
            }
        }

        private void OnJoined(Proto.JoinResponse r)
        {
            lock (gate)
            {
                seat = (int)r.Seat;
                setup = r.Setup;
                if (r.Ready) ready = true;
            }
        }

        private void OnInput(Proto.InputDatagram dg)
        {
            var win = new List<InputPacket>(dg.Inputs.Count);
            foreach (var ni in dg.Inputs)
                win.Add(NetWire.FromNetInput(ni));

            lock (gate)
            {
                w.RecordPeerAck((int)dg.Ack);    // 学习对端 ack，裁剪本端重发窗口（省带宽）
                pending.AddRange(w.Remote(win)); // 去重由共享 Windower 完成
            }
        }

        private void SendJoin() => Write(new Proto.Packet { Join = join });

        private void Write(Proto.Packet pkt)
        {
            byte[] b;
            try { b = pkt.ToByteArray(); }
            catch { return; }
            try { conn.Send(b, b.Length); }
            catch { /* 关闭中/瞬时错误：忽略，靠冗余窗口与重发兜底 */ }
        }

        private static string NewClientId()
        {
            var b = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);
            var sb = new StringBuilder(16);
            for (int i = 0; i < b.Length; i++)
                sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>停止收包线程并关闭 socket。</summary>
        public void Dispose()
        {
            running = false;
            try { conn.Close(); } catch { }
            try { recvThread?.Join(500); } catch { }
        }
    }
}
