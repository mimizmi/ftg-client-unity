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

        public static MotionPattern LP(string id, ButtonMask trigger, int priority = 10) => new MotionPattern
        {
            Id = id, Priority = priority, TriggerButtons = trigger, TotalWindow = 15,
            Steps = new[] { S(Numpad.Mask(4)), S(Numpad.Mask(2)), S(Numpad.Mask(4,2,6), gap: 10) },
        };
        
        public static MotionPattern DashBackward(int priority = 5) => new MotionPattern
        {
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
            Id = "DASH_F", Priority = priority, TriggerButtons = ButtonMask.None, TotalWindow = 14,
            Steps = new[]
            {
                S(Numpad.Mask(6), gap: 8),
                S(Numpad.Mask(5, 2, 8), gap: 8), // 中间必须离开"前"，否则按住前不会误判成冲刺
                S(Numpad.Mask(6), gap: 8),
            },
        };
    }
}