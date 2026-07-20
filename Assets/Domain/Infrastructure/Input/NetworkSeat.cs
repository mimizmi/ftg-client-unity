using System.Collections.Generic;
using Domain.Infrastructure;
using Domain.Infrastructure.Motion;

namespace Domain.Infrastructure.Input
{
    /// <summary>
    /// 帧同步/回滚（N5）的座位：不自采输入，而是消费“已确认”的逐帧输入——本地帧由本端采样后确认，
    /// 远端帧由网络包到达后确认。核心（FighterState/Movement）完全不知道输入来自设备还是网线，
    /// 这正是回放/帧同步/回滚共用同一 IInputSeat 契约的意义。这是 Go 侧 seat.NetworkSeat 的移植。
    ///
    /// 与 ScriptedSeat/AiSeat 逐字一致的加工顺序（推缓冲 → 队列过期 → 搓招检测 → 指令入队）
    /// 是确定性前提：三种座位喂给核心的东西必须无法区分。
    ///
    /// inbox 按【绝对帧号】索引已确认输入。回滚驱动保证只在双方座位都 Confirmed(N) 后才 Tick——
    /// ManualTick 落到未确认帧属驱动 bug，此处退化为中立仅作防御。
    /// </summary>
    public sealed class NetworkSeat : IInputSeat
    {
        public InputBuffer Buffer { get; }
        public CommandQueue Commands { get; }
        public MotionDetector Detector { get; }
        public bool FacingRight { get; set; } = true;
        public bool SelfDriven { get; set; }
        public int CurrentFrame { get; private set; }

        private readonly Dictionary<int, InputFrame> inbox = new Dictionary<int, InputFrame>();
        private readonly List<MotionPattern> detectResults = new List<MotionPattern>(4);

        /// <summary>缓冲容量 120，与 ScriptedSeat/AiSeat 一致。</summary>
        public NetworkSeat()
        {
            Buffer = new InputBuffer(120);
            Commands = new CommandQueue();
            Detector = new MotionDetector();
        }

        // 回滚克隆用：缓冲/队列已深拷贝传入，detector 不可变共享。
        private NetworkSeat(InputBuffer buffer, CommandQueue commands, MotionDetector detector)
        {
            Buffer = buffer;
            Commands = commands;
            Detector = detector;
        }

        /// <summary>确认绝对帧号 frame 的输入（本地采样或远端到达都走这里）。幂等：同帧同输入重复确认无副作用。</summary>
        public void Confirm(int frame, InputFrame f)
        {
            f.Frame = frame;
            inbox[frame] = f;
        }

        /// <summary>报告绝对帧号 frame 的输入是否已就位。驱动用它决定能否推进。</summary>
        public bool Confirmed(int frame) => inbox.ContainsKey(frame);

        /// <summary>
        /// 深快照座位（回滚存档）：缓冲/指令队列深拷贝，inbox 复制一份，detector 不可变共享。
        /// 回滚还原后重新 Confirm+Tick 即可从这一帧确定地重放。与 Go 侧 NetworkSeat.Clone 对称。
        /// </summary>
        public NetworkSeat Clone()
        {
            var ns = new NetworkSeat(Buffer.Clone(), Commands.Clone(), Detector)
            {
                FacingRight = FacingRight,
                SelfDriven = SelfDriven,
                CurrentFrame = CurrentFrame,
            };
            foreach (KeyValuePair<int, InputFrame> kv in inbox)
                ns.inbox[kv.Key] = kv.Value;
            return ns;
        }

        public void ManualTick()
        {
            CurrentFrame++;

            if (!inbox.TryGetValue(CurrentFrame, out InputFrame f))
                f = new InputFrame { Frame = CurrentFrame, Direction = 5 }; // 防御：不该发生（见类型注释）
            Buffer.Push(f);
            inbox.Remove(CurrentFrame); // 已消费，回滚重放时会重新 Confirm

            Commands.Tick(CurrentFrame);
            Detector.DetectAll(Buffer, FacingRight, detectResults);
            for (int i = 0; i < detectResults.Count; i++)
                Commands.Enqueue(detectResults[i].Id, detectResults[i].Priority, CurrentFrame);
        }
    }
}
