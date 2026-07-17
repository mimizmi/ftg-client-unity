using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Replay
{
    /// <summary>
    /// 回放录制器：挂在 TickFinished 上，每帧抄录双方座位刚被模拟消费的输入。
    /// 从 InputHistory.Latest 取而不是自己再采样——录的是"模拟看到的"，
    /// 不是"设备发生的"，两者在锁存/丢帧边界上可能不同，前者才可复现。
    /// 冻结阶段（Intro/RoundOver）也在录：座位每帧都在采样，输入流不能有洞。
    /// </summary>
    public sealed class ReplayRecorder
    {
        public ReplayData Data { get; }

        private readonly BattleSimulation sim;
        private bool recording;

        public ReplayRecorder(BattleSimulation sim, string p1CharacterId, string p2CharacterId)
        {
            this.sim = sim;
            Data = new ReplayData
            {
                P1CharacterId = p1CharacterId,
                P2CharacterId = p2CharacterId,
                Config = sim.Config,
            };
            sim.TickFinished += OnTick;
            recording = true;
        }

        /// <summary>停录（幂等）。之后 Data 即完整回放。</summary>
        public void Stop()
        {
            if (!recording) return;
            sim.TickFinished -= OnTick;
            recording = false;
        }

        private void OnTick(int frame)
        {
            InputFrame p1 = sim.P1.InputHistory.Latest;
            InputFrame p2 = sim.P2.InputHistory.Latest;
            Data.Frames.Add(new ReplayInputFrame
            {
                P1Direction = p1.Direction,
                P1Held = p1.Held,
                P1Pressed = p1.Pressed,
                P2Direction = p2.Direction,
                P2Held = p2.Held,
                P2Pressed = p2.Pressed,
            });
        }
    }
}
