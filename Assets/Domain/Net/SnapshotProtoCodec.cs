using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Proto = FTG.Net.Proto;

namespace Domain.Net
{
    /// <summary>
    /// 定点状态 ↔ protobuf 快照适配：把角色/整局的【确定性状态】搬进 protobuf。
    /// 用于 N4 对拍分歧诊断（哈希对不上时逐字段 diff 定位）与 N5 回滚存档。
    ///
    /// 两条对齐纪律：
    ///   · 定点上线：<see cref="FixVec2"/> 走 sfixed32 存 <c>Fix.Raw</c>，逐位无损，绝不转 float。
    ///   · 枚举序数对齐：proto 枚举与 C# byte 枚举逐值同序，转换只是一次整型 cast，不查名字。
    ///     （<c>ProtoCodecTests</c> 有守卫断言，改 C# 枚举顺序而不同步改 .proto 即红。）
    /// </summary>
    public static class SnapshotProtoCodec
    {
        // ---- FixVec2 ↔ Proto.FixVec2（sfixed32 存 Raw，逐位无损）----

        public static Proto.FixVec2 ToProto(FixVec2 v) => new Proto.FixVec2 { XRaw = v.X.Raw, YRaw = v.Y.Raw };

        public static FixVec2 FromProto(Proto.FixVec2 v) => new FixVec2(Fix.FromRaw(v.XRaw), Fix.FromRaw(v.YRaw));

        // ---- FighterState → Proto.FighterSnapshot（确定性字段集 = 哈希字段集）----

        public static Proto.FighterSnapshot ToProto(FighterState f) => new Proto.FighterSnapshot
        {
            Position = ToProto(f.Position),
            Health = f.Health,
            Status = (Proto.FighterStatus)(int)f.Status,
            MoveFrame = f.MoveFrame,
            StunRemaining = f.StunRemaining,
            MovementState = (Proto.MovementState)(int)f.Movement.State,
            MotionFrame = f.Movement.MotionFrame,
            FacingRight = f.FacingRight,
            CurrentMoveId = f.CurrentMove?.MoveId ?? "", // 空串 = C# 侧 null（无当前招式）
        };

        // ---- BattleSimulation → Proto.BattleSnapshot（整局 + 该帧规范哈希）----

        public static Proto.BattleSnapshot ToProto(BattleSimulation sim) => new Proto.BattleSnapshot
        {
            Frame = (uint)sim.CurrentFrame,
            P1 = ToProto(sim.P1),
            P2 = ToProto(sim.P2),
            StateHash = StateHasher.HashState(sim),
        };
    }
}
