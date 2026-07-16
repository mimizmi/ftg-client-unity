using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Motion
{
    public sealed class MotionStep
    {
        public ushort DirMask;
        public int MaxGap = 8;
        public int ChargeFrames = 0;
    }
    public sealed class MotionPattern
    {
        public string Id;
        public int Priority;
        public MotionStep[] Steps;
        public ButtonMask TriggerButtons;
        public int TotalWindow = 15;
        public bool MirrorByFacing = true;
    }

    public sealed class MotionLibrary
    {
        private static MotionStep S(ushort dirs, int gap = 8, int charge = 0)
        => new MotionStep {DirMask = dirs, MaxGap = gap, ChargeFrames = charge};

        // 窗口哲学：TotalWindow 是"整条指令从第一个方向到触发"的总预算。
        // 原 15/16 帧(约250ms)偏竞技向，手感发紧；主流游戏宽容度在 20~30 帧量级。
        // 统一放到 22 帧(367ms)：搓慢一点也能出，误判率靠 MaxGap(逐步间隔上限)兜底。

        /// <summary>波动拳 236（Quarter Circle Forward）：下(2) → 前下(3) → 前(6)。</summary>
        public static MotionPattern Qcf(string id, ButtonMask trigger, int priority = 100) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 22,
            Steps = new[] { S(Numpad.Mask(2)), S(Numpad.Mask(3)), S(Numpad.Mask(6)) },
        };

        /// <summary>反向波 214（Quarter Circle Back）：下(2) → 后下(1) → 后(4)。</summary>
        public static MotionPattern Qcb(string id, ButtonMask trigger, int priority = 100) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 22,
            Steps = new[] { S(Numpad.Mask(2)), S(Numpad.Mask(1)), S(Numpad.Mask(4)) },
        };

        /// <summary>升龙 623（Dragon Punch）：前(6) → 下(2) → 前下(3)。优先级高于波动，歧义时升龙优先。</summary>
        public static MotionPattern Dp(string id, ButtonMask trigger, int priority = 110) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 22,
            Steps = new[] { S(Numpad.Mask(6)), S(Numpad.Mask(2)), S(Numpad.Mask(3)) },
        };
        
        public static MotionPattern DashBackward(int priority = 5) => new MotionPattern
        {
            // Id 是【指令名】，不是招式名——MovementController 按 "DASH_B" 取它，再触发招式 config.BackDashId
            Id = "DASH_B", Priority = priority, TriggerButtons = ButtonMask.None, TotalWindow = 18, // 14→18：双击稍慢也能出

            Steps = new[]
            {
                S(Numpad.Mask(4), gap: 8),
                S(Numpad.Mask(5, 2, 8), gap: 8),
                S(Numpad.Mask(4), gap: 8),
            },
        };
        
        public static MotionPattern DashForward(int priority = 5) => new MotionPattern
        {
            // Id 是【指令名】，不是招式名——MovementController 按 "DASH_F" 取它，再触发招式 config.DashId
            Id = "DASH_F", Priority = priority, TriggerButtons = ButtonMask.None, TotalWindow = 18, // 14→18：双击稍慢也能出

            Steps = new[]
            {
                S(Numpad.Mask(6), gap: 8),
                S(Numpad.Mask(5, 2, 8), gap: 8), // 中间必须离开"前"，否则按住前不会误判成冲刺
                S(Numpad.Mask(6), gap: 8),
            },
        };

        // 注：跳跃【不是】搓招——它是"按住带上的方向"这种瞬时输入，由 MovementController
        // 直接读方向处理（9=前跳 7=后跳 8=垂直跳），不走 MotionDetector/指令队列。
    }
}