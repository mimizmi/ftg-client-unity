using System.Linq;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Motion;
using Proto = FTG.Net.Proto;

namespace Domain.Net
{
    /// <summary>
    /// 角色定义 → protobuf 夹具（<c>ftg.v1.FighterDefinitionDef</c>）。
    /// 夹具是跨语言对拍的静态数据源：Go 侧只读它，不复刻数值——
    /// 静态数据零分歧，对拍纯粹验证逐帧逻辑。
    ///
    /// 【导出的是注入 JSON 前的纯代码数据】BoxTracks/RootMotion 不进夹具，
    /// 两端各自从 Assets/BoxData/*.json 装载（C# BoxDataLoader / Go sim/content）。
    ///
    /// 【字节稳定】守卫测试按字节比对夹具是否过期，故 ReactionMoves 按序数升序写入
    /// （proto map 序列化序不稳定，协议里用 repeated 代替）。
    ///
    /// 【前瞻约束】MoveEntry.Condition（Func）带不动 lambda——将来加气槽等条件时
    /// 必须以数据字段表达（协议加 require_meter 之类），不得写闭包，否则对拍断链。
    /// </summary>
    public static class FighterDefinitionProtoCodec
    {
        public static Proto.FighterDefinitionDef ToProto(FighterDefinition def)
        {
            var msg = new Proto.FighterDefinitionDef
            {
                CharacterId = def.CharacterId ?? "",
                Movement = ToProto(def.Movement ?? new MovementConfig()),
            };

            foreach (MotionPattern p in def.Motions)
                msg.Motions.Add(ToProto(p));

            foreach (MoveEntry e in def.MoveEntries)
                msg.MoveEntries.Add(ToProto(e));

            foreach (MoveData m in def.Moves)
                msg.Moves.Add(ToProto(m));

            // 按序数升序：保证同一定义 → 同一字节序列（守卫测试的前提）
            foreach (var kv in def.ReactionMoves.OrderBy(kv => (uint)kv.Key))
            {
                msg.ReactionMoves.Add(new Proto.ReactionMoveDef
                {
                    Reaction = (uint)kv.Key,
                    MoveId = kv.Value ?? "",
                });
            }
            return msg;
        }

        private static Proto.MotionPatternDef ToProto(MotionPattern p)
        {
            var msg = new Proto.MotionPatternDef
            {
                Id = p.Id ?? "",
                Priority = p.Priority,
                TriggerButtons = (uint)p.TriggerButtons,
                TotalWindow = p.TotalWindow,
                MirrorByFacing = p.MirrorByFacing,
            };
            foreach (MotionStep s in p.Steps)
            {
                msg.Steps.Add(new Proto.MotionStepDef
                {
                    DirMask = s.DirMask,
                    MaxGap = s.MaxGap,
                    ChargeFrames = s.ChargeFrames,
                });
            }
            return msg;
        }

        private static Proto.MoveEntryDef ToProto(MoveEntry e)
        {
            var msg = new Proto.MoveEntryDef
            {
                CommandId = e.CommandId ?? "",
                Buttons = (uint)e.Buttons,
                MoveId = e.MoveId ?? "",
                Stance = (Proto.Stance)(int)e.Stance,
                Priority = e.Priority,
                CancelOnly = e.CancelOnly,
            };
            if (e.CancelFrom != null) msg.CancelFrom.AddRange(e.CancelFrom);
            if (e.FeintFrom != null) msg.FeintFrom.AddRange(e.FeintFrom);
            return msg;
        }

        private static Proto.MoveDataDef ToProto(MoveData m) => new Proto.MoveDataDef
        {
            MoveId = m.MoveId ?? "",
            Startup = m.Startup,
            Active = m.Active,
            Recovery = m.Recovery,
            Attributes = (uint)m.Attributes,
            Damage = m.Damage,
            HitstunFrames = m.HitstunFrames,
            Hitstop = m.Hitstop,
            Reaction = (uint)m.Reaction,
            CancelFrom = m.CancelFrom,
            InvulnFrom = m.InvulnFrom,
            InvulnTo = m.InvulnTo,
            CatchFrom = m.CatchFrom,
            CatchTo = m.CatchTo,
            CatchMask = (uint)m.CatchMask,
            CatchFollowupMoveId = m.CatchFollowupMoveId ?? "",
        };

        private static Proto.MovementConfigDef ToProto(MovementConfig c) => new Proto.MovementConfigDef
        {
            IdleId = c.IdleId ?? "",
            CrouchId = c.CrouchId ?? "",
            CrouchEnterId = c.CrouchEnterId ?? "",
            CrouchExitId = c.CrouchExitId ?? "",
            WalkForwardId = c.WalkForwardId ?? "",
            WalkBackwardId = c.WalkBackwardId ?? "",
            DashId = c.DashId ?? "",
            BackDashId = c.BackDashId ?? "",
            RunId = c.RunId ?? "",
            JumpNeutralId = c.JumpNeutralId ?? "",
            JumpForwardId = c.JumpForwardId ?? "",
            JumpBackwardId = c.JumpBackwardId ?? "",
            AirDashId = c.AirDashId ?? "",
            AirDashCount = c.AirDashCount,
        };
    }
}
