using System;
using System.Collections.Generic;
using Domain.Infrastructure;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;

namespace FTG.Tests
{
    /// <summary>一帧脚本输入：方向（世界系 Numpad）+ 按住的键。边沿由座位推导。</summary>
    public readonly struct ScriptedInput
    {
        public readonly byte Direction;
        public readonly ButtonMask Held;

        public ScriptedInput(byte direction, ButtonMask held = ButtonMask.None)
        {
            Direction = direction;
            Held = held;
        }
    }

    /// <summary>
    /// 脚本化输入座位：用纯函数 (帧号 → 输入) 替代真实设备，驱动完整模拟。
    /// ManualTick 的处理顺序（采样 → 队列过期 → 搓招检测 → 指令入队）
    /// 与 FightingInputController.GamePlayLogicTick 一致——两者喂给核心的东西
    /// 必须无法区分，这正是 IInputSeat 的契约。也是将来回放/假人座位的原型。
    /// </summary>
    public sealed class ScriptedSeat : IInputSeat
    {
        public InputBuffer Buffer { get; } = new InputBuffer(120);
        public CommandQueue Commands { get; } = new CommandQueue();
        public MotionDetector Detector { get; } = new MotionDetector();
        public bool FacingRight { get; set; } = true;
        public bool SelfDriven { get; set; }
        public int CurrentFrame { get; private set; }

        private readonly Func<int, ScriptedInput> script;
        private readonly List<MotionPattern> detectResults = new List<MotionPattern>(4);
        private ButtonMask prevHeld;

        public ScriptedSeat(Func<int, ScriptedInput> script)
        {
            this.script = script;
        }

        public void ManualTick()
        {
            CurrentFrame++;
            ScriptedInput s = script(CurrentFrame);

            ButtonMask held = s.Held;
            Buffer.Push(new InputFrame
            {
                Frame = CurrentFrame,
                Direction = s.Direction,
                Held = held,
                Pressed = held & ~prevHeld,
                Released = prevHeld & ~held,
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
