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

        /// <summary>波动拳 236（Quarter Circle Forward）：下(2) → 前下(3) → 前(6)。</summary>
        public static MotionPattern Qcf(string id, ButtonMask trigger, int priority = 100) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 15,
            Steps = new[] { S(Numpad.Mask(2)), S(Numpad.Mask(3)), S(Numpad.Mask(6)) },
        };

        /// <summary>反向波 214（Quarter Circle Back）：下(2) → 后下(1) → 后(4)。</summary>
        public static MotionPattern Qcb(string id, ButtonMask trigger, int priority = 100) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 15,
            Steps = new[] { S(Numpad.Mask(2)), S(Numpad.Mask(1)), S(Numpad.Mask(4)) },
        };

        /// <summary>升龙 623（Dragon Punch）：前(6) → 下(2) → 前下(3)。优先级高于波动，歧义时升龙优先。</summary>
        public static MotionPattern Dp(string id, ButtonMask trigger, int priority = 110) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 16,
            Steps = new[] { S(Numpad.Mask(6)), S(Numpad.Mask(2)), S(Numpad.Mask(3)) },
        };
        
        public static MotionPattern DashBackward(int priority = 5) => new MotionPattern
        {
            // Id 是【指令名】，不是招式名——MovementController 按 "DASH_B" 取它，再触发招式 config.BackDashId
            Id = "DASH_B", Priority = priority, TriggerButtons = ButtonMask.None, TotalWindow = 14,
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
            Id = "DASH_F", Priority = priority, TriggerButtons = ButtonMask.None, TotalWindow = 14,
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