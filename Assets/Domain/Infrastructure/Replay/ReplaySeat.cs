using System.Collections.Generic;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;

namespace Domain.Infrastructure.Replay
{
    /// <summary>
    /// 回放座位：把 ReplayData 的一侧输入按帧喂回模拟。
    /// ManualTick 的管线（压帧 → 队列过期 → 搓招检测 → 指令入队）与真实座位
    /// FightingInputController.GamePlayLogicTick 逐步对齐——检测器是"缓冲内容 + 朝向"
    /// 的纯函数，喂同样的帧就产出同样的指令，这就是回放不需要录指令的原因。
    /// 帧耗尽后持续输出空闲（5 方向无键），战斗自然停摆等外部收场。
    /// </summary>
    public sealed class ReplaySeat : IInputSeat
    {
        public InputBuffer Buffer { get; } = new InputBuffer(120);
        public CommandQueue Commands { get; } = new CommandQueue();
        public MotionDetector Detector { get; } = new MotionDetector();
        public bool FacingRight { get; set; } = true;
        public bool SelfDriven { get; set; } // 回放由 BattleLoop 统一驱动，此开关无实义

        /// <summary>已回放到的帧号（1 起）。</summary>
        public int CurrentFrame { get; private set; }

        public bool Exhausted => CurrentFrame >= data.FrameCount;

        private readonly ReplayData data;
        private readonly bool isP1;
        private readonly List<MotionPattern> detectResults = new List<MotionPattern>(4);
        private ButtonMask prevHeld;

        public ReplaySeat(ReplayData data, bool isP1)
        {
            this.data = data;
            this.isP1 = isP1;
        }

        public void ManualTick()
        {
            CurrentFrame++;

            byte dir = 5;
            ButtonMask held = ButtonMask.None;
            ButtonMask pressed = ButtonMask.None;
            if (CurrentFrame <= data.FrameCount)
            {
                ReplayInputFrame f = data.Frames[CurrentFrame - 1];
                dir = isP1 ? f.P1Direction : f.P2Direction;
                held = isP1 ? f.P1Held : f.P2Held;
                pressed = isP1 ? f.P1Pressed : f.P2Pressed;
            }

            Buffer.Push(new InputFrame
            {
                Frame = CurrentFrame,
                Direction = dir,
                Held = held,
                Pressed = pressed,
                Released = prevHeld & ~held, // Released 不入档：由 Held 序列重建
            });
            prevHeld = held;

            Commands.Tick(CurrentFrame);
            Detector.DetectAll(Buffer, FacingRight, detectResults);
            for (int i = 0; i < detectResults.Count; i++)
            {
                MotionPattern p = detectResults[i];
                Commands.Enqueue(p.Id, p.Priority, CurrentFrame);
            }
        }
    }
}
