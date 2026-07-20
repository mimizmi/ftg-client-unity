using System;
using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;

namespace Domain.Net.Transport
{
    /// <summary>本端设备一帧的采样：方向（世界系 Numpad）+ 按住的键。边沿(Pressed/Released)由驱动推导。</summary>
    public struct LocalInput
    {
        public byte Direction;
        public ButtonMask Held;

        public LocalInput(byte direction, ButtonMask held)
        {
            Direction = direction;
            Held = held;
        }
    }

    /// <summary>
    /// 回滚驱动（N5-② rollback 的 C# 移植，对应 Go 侧 lockstep.RollbackPeer）。相对帧同步（需等对方
    /// 输入到齐才推进，延迟直接变卡顿），回滚让【本地输入立即生效】（inputDelay 可为 0），远端未到的输入用
    /// “预测”（重复上一帧）先跑，真输入到达后若与预测不符，就【还原到确认帧快照、用真输入重模拟】到当前帧——
    /// 延迟被藏成偶发的画面回跳，而非持续的输入迟滞。这是格斗游戏联机的黄金标准（GGPO 家族）。
    ///
    /// 两份模拟：
    ///   · confirmed：权威，仅【双方真输入】按帧序推进、永不回退。逐帧 StateHash = 最终确定轨迹，
    ///     必须与单机参照、与对端彼此逐位一致。
    ///   · predicted：每墙钟帧从 confirmed 深克隆（存档/还原）再用预测输入跑到 head（本地已采样最远帧），
    ///     供“当前画面”。视图逐帧轮询它的状态。
    ///
    /// 核心正确性主张：无论预测错多少、回滚多少帧，confirmed 轨迹恒等于单机参照——回滚只改“何时看到
    /// 正确结果”，绝不改“最终的正确结果”。StateHasher 是现成 desync 探针。
    /// </summary>
    public sealed class RollbackDriver
    {
        private readonly BattleSimulation confirmed;
        private BattleSimulation predicted;
        private readonly ITransport transport;
        private readonly Func<int, LocalInput> poll;
        private readonly bool localIsP1;
        private readonly int delay;

        private int wallTick;
        private ButtonMask prevHeld;
        private readonly Dictionary<int, InputFrame> realLocal = new Dictionary<int, InputFrame>();
        private readonly Dictionary<int, InputFrame> realRemote = new Dictionary<int, InputFrame>();
        private int remoteReal; // 已连续就位的最高远端真帧
        private int localReal;  // 已采样的最高本地帧 = wallTick + delay

        private readonly List<ulong> confirmedTrace = new List<ulong>();
        private readonly Dictionary<int, ulong> predictedHash = new Dictionary<int, ulong>();

        /// <summary>预测哈希 ≠ 最终确认哈希的帧数（真·误预测被回滚修正）。</summary>
        public int Corrections { get; private set; }

        /// <summary>单帧重模拟的最大窗口 = head − confirmedFrame（≈ 延迟深度）。</summary>
        public int MaxRollback { get; private set; }

        /// <summary>
        /// confirmed 必须是一局【两座位皆 NetworkSeat】的确定性模拟（由组合根/工厂装配）。
        /// poll 是本端设备采样（帧号 → 方向+按键）；localIsP1 决定本端占 P1(true)/P2(false)；
        /// inputDelay=D 回滚通常 0。
        /// </summary>
        public RollbackDriver(BattleSimulation confirmed, ITransport transport,
            Func<int, LocalInput> poll, bool localIsP1, int inputDelay = 0)
        {
            this.confirmed = confirmed;
            this.transport = transport;
            this.poll = poll;
            this.localIsP1 = localIsP1;
            delay = inputDelay;
            Prime();
            predicted = confirmed.Clone(CloneSeat); // 初始预测 = 空局克隆
        }

        private static IInputSeat CloneSeat(IInputSeat s) => ((NetworkSeat)s).Clone();

        // 播种输入延迟窗口：帧 1..D 双方皆中立、皆视为真（两端同约定），并记为已就位。
        private void Prime()
        {
            var neutral = new InputFrame { Direction = 5 };
            for (int f = 1; f <= delay; f++)
            {
                realLocal[f] = neutral;
                realRemote[f] = neutral;
            }
            remoteReal = delay;
            localReal = delay;
        }

        /// <summary>走一墙钟帧：采样本地 → 收远端 → 推进 confirmed（真输入）→ 从 confirmed 重建预测。</summary>
        public void Advance()
        {
            // ① 采样本地输入。回滚下本地立即生效：sim 帧 = wallTick + D（D 通常 0）。
            wallTick++;
            LocalInput li = poll(wallTick);
            var frame = new InputFrame
            {
                Direction = li.Direction,
                Held = li.Held,
                Pressed = li.Held & ~prevHeld,
                Released = prevHeld & ~li.Held,
            };
            prevHeld = li.Held;
            int simFrame = wallTick + delay;
            frame.Frame = simFrame;
            realLocal[simFrame] = frame;
            localReal = simFrame;
            transport.Send(new InputPacket(simFrame, frame));

            // ② 收远端真输入，扩展连续就位区间。
            foreach (InputPacket pkt in transport.Drain())
                realRemote[pkt.Frame] = pkt.Input;
            while (realRemote.ContainsKey(remoteReal + 1)) remoteReal++;

            // ③ 用双方真输入把 confirmed 尽可能往前推（这些帧从此定稿、永不回退）。
            AdvanceConfirmed();

            // ④ 从 confirmed 深克隆预测 sim，用预测输入跑到 head。
            BuildPredicted();
        }

        // 只要下一帧双方真输入都在，就把权威模拟推进一帧并定稿其哈希；若该帧此前的预测哈希与最终不同，计一次修正。
        private void AdvanceConfirmed()
        {
            while (true)
            {
                int f = confirmed.CurrentFrame + 1;
                if (!realLocal.TryGetValue(f, out InputFrame lin)) break;
                if (!realRemote.TryGetValue(f, out InputFrame rin)) break;
                Assign(lin, rin, out InputFrame p1in, out InputFrame p2in);
                Drive(confirmed, f, p1in, p2in);
                ulong h = StateHasher.HashState(confirmed);
                confirmedTrace.Add(h);
                if (predictedHash.TryGetValue(f, out ulong ph) && ph != h)
                    Corrections++; // 预测错过、已被真输入纠正
            }
        }

        // 存档/还原：从 confirmed 克隆，重模拟 confirmedFrame+1..head。head 之外的远端帧用“重复上一帧”预测；
        // 首次以预测跑出的帧哈希留存，供事后判定是否被修正。
        private void BuildPredicted()
        {
            int head = localReal;
            int baseFrame = confirmed.CurrentFrame;
            int window = head - baseFrame;
            if (window > MaxRollback) MaxRollback = window;

            predicted = confirmed.Clone(CloneSeat);
            for (int f = baseFrame + 1; f <= head; f++)
            {
                InputFrame lin = realLocal[f]; // head 内本地帧必在
                bool real = realRemote.TryGetValue(f, out InputFrame rin);
                if (!real) rin = PredictRemote();
                Assign(lin, rin, out InputFrame p1in, out InputFrame p2in);
                Drive(predicted, f, p1in, p2in);
                if (!real && !predictedHash.ContainsKey(f))
                    predictedHash[f] = StateHasher.HashState(predicted);
            }
        }

        // 重复最后一个连续就位的远端真输入（重复上一帧是最简且实战有效的预测器）。
        private InputFrame PredictRemote()
        {
            if (remoteReal == 0) return new InputFrame { Direction = 5 };
            return realRemote[remoteReal];
        }

        // 按本端所在座位把 (本地,远端) 输入映射到 (P1,P2)。
        private void Assign(InputFrame local, InputFrame remote, out InputFrame p1, out InputFrame p2)
        {
            if (localIsP1) { p1 = local; p2 = remote; }
            else { p1 = remote; p2 = local; }
        }

        // 用给定双方输入把某模拟推进一帧（前置：sim.CurrentFrame == frame-1）。座位就地 Confirm。
        private static void Drive(BattleSimulation sim, int frame, InputFrame p1in, InputFrame p2in)
        {
            ((NetworkSeat)sim.P1.InputController).Confirm(frame, p1in);
            ((NetworkSeat)sim.P2.InputController).Confirm(frame, p2in);
            sim.Tick();
        }

        // ---- 暴露给测试与展示层 ----
        public int ConfirmedFrame => confirmed.CurrentFrame;
        public int HeadFrame => predicted.CurrentFrame;
        public IReadOnlyList<ulong> ConfirmedTrace => confirmedTrace;

        /// <summary>“当前画面”模拟：视图每帧从这里读位置/血量/动画状态（而非订阅事件）。</summary>
        public BattleSimulation Predicted => predicted;
    }
}
