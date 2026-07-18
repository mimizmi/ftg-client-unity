using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;

namespace Domain.Infrastructure.Input
{
    /// <summary>
    /// 内置假人策略库（训练场 F1-F4 对应四档）。
    /// 全部是入参纯函数——节奏用帧号取模表达（确定性），绝不用随机数；
    /// 距离判断走定点（N2 起策略读的 Position 就是定点，天然一致）。
    /// </summary>
    public static class DummyPolicies
    {
        public static readonly IDummyPolicy Idle = new IdlePolicy();
        public static readonly IDummyPolicy Crouch = new CrouchPolicy();
        public static readonly IDummyPolicy WalkBack = new WalkBackPolicy();
        public static readonly IDummyPolicy SimpleCpu = new SimpleCpuPolicy();

        private sealed class IdlePolicy : IDummyPolicy
        {
            public DummyInput Decide(int frame, FighterState self, FighterState opponent)
                => new DummyInput(5);
        }

        private sealed class CrouchPolicy : IDummyPolicy
        {
            public DummyInput Decide(int frame, FighterState self, FighterState opponent)
                => new DummyInput(2);
        }

        private sealed class WalkBackPolicy : IDummyPolicy
        {
            public DummyInput Decide(int frame, FighterState self, FighterState opponent)
            {
                // 方向是世界系：对手在右就往左(4)退，反之往右(6)退
                bool opponentOnRight = opponent.Position.X >= self.Position.X;
                return new DummyInput(opponentOnRight ? (byte)4 : (byte)6);
            }
        }

        /// <summary>
        /// 简单 CPU：够不着就走近，到位后按节拍拳脚交替戳（帧号取模的确定性节奏）。
        /// 目的不是强——是当陪练 + 给 AiSeat 确定性测试当活体样本。
        /// </summary>
        private sealed class SimpleCpuPolicy : IDummyPolicy
        {
            // 大致一个轻拳的够得着距离（1.1，定点分数——不用 FromFloat(1.1f)，语义更明确）
            private static readonly Fix PokeRange = Fix.FromFraction(11, 10);

            public DummyInput Decide(int frame, FighterState self, FighterState opponent)
            {
                bool opponentOnRight = opponent.Position.X >= self.Position.X;
                Fix distance = Fix.Abs(opponent.Position.X - self.Position.X);

                if (distance > PokeRange)
                    return new DummyInput(opponentOnRight ? (byte)6 : (byte)4);

                // 到位：每 48 帧一轮——开头 2 帧按轻拳，中点 2 帧按轻脚（按住 2 帧确保下降沿被采到）
                int beat = frame % 48;
                if (beat < 2) return new DummyInput(5, ButtonMask.LP);
                if (beat >= 24 && beat < 26) return new DummyInput(5, ButtonMask.LK);
                return new DummyInput(5);
            }
        }
    }
}
