using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Motion;

namespace Domain.Infrastructure.Input
{
    /// <summary>一帧假人决策：方向（世界系 Numpad）+ 按住的键。边沿由座位推导。</summary>
    public readonly struct DummyInput
    {
        public readonly byte Direction;
        public readonly ButtonMask Held;

        public DummyInput(byte direction, ButtonMask held = ButtonMask.None)
        {
            Direction = direction;
            Held = held;
        }
    }

    /// <summary>
    /// 假人策略：纯函数 (帧号, 自己, 对手) → 本帧输入。
    /// 【确定性纪律】只准依赖入参——禁止 Time/Random/任何外部可变状态；
    /// 同一帧同一局面必须给出同一输入，否则回放/未来回滚全崩（有测试双跑钉死）。
    /// </summary>
    public interface IDummyPolicy
    {
        DummyInput Decide(int frame, FighterState self, FighterState opponent);
    }

    /// <summary>
    /// AI/假人座位：IInputSeat 的第四个实现（真人/脚本/回放之后）。
    /// 决策只【读】模拟状态，经与玩家完全相同的输入管线进入模拟——AI 没有后门：
    /// 想出招同样要产生按键、过搓招检测器（管线与 ScriptedSeat/ReplaySeat 逐步对齐）。
    /// Attach 在装配完成后注入双方状态引用（座位先于角色构造，构造期拿不到）。
    /// </summary>
    public sealed class AiSeat : IInputSeat
    {
        public InputBuffer Buffer { get; } = new InputBuffer(120);
        public CommandQueue Commands { get; } = new CommandQueue();
        public MotionDetector Detector { get; } = new MotionDetector();
        public bool FacingRight { get; set; } = true;
        public bool SelfDriven { get; set; } // 由 BattleLoop 统一驱动，此开关无实义
        public int CurrentFrame { get; private set; }

        /// <summary>运行期可切换（训练场切假人行为）：只影响将来帧的输入，确定性无碍。</summary>
        public IDummyPolicy Policy { get; set; }

        private FighterState self;
        private FighterState opponent;
        private readonly List<MotionPattern> detectResults = new List<MotionPattern>(4);
        private ButtonMask prevHeld;

        public AiSeat(IDummyPolicy policy)
        {
            Policy = policy;
        }

        /// <summary>装配完成后由组合根调用，接上"我是谁、对手是谁"。</summary>
        public void Attach(FighterState self, FighterState opponent)
        {
            this.self = self;
            this.opponent = opponent;
        }

        public void ManualTick()
        {
            CurrentFrame++;

            DummyInput d = Policy != null && self != null
                ? Policy.Decide(CurrentFrame, self, opponent)
                : new DummyInput(5);

            ButtonMask held = d.Held;
            Buffer.Push(new InputFrame
            {
                Frame = CurrentFrame,
                Direction = d.Direction,
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
