package content

import (
	"os"

	"google.golang.org/protobuf/proto"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/combat"
	"ftgserver/sim/input"
	"ftgserver/sim/motion"
)

// LoadDefinition 读取 C# 导出的角色定义夹具（FighterDefinitionDef 二进制）并组装为
// 运行时结构。夹具是【注入 JSON 前】的纯代码数据——调用方随后应执行
// content.Apply(def.Moves, boxes, rootmotion) 注入判定框与位移，与 C# 管线同构。
func LoadDefinition(path string) (*combat.FighterDefinition, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var pb ftgv1.FighterDefinitionDef
	if err := proto.Unmarshal(raw, &pb); err != nil {
		return nil, err
	}
	return fromProto(&pb), nil
}

func fromProto(pb *ftgv1.FighterDefinitionDef) *combat.FighterDefinition {
	def := &combat.FighterDefinition{
		CharacterID:   pb.GetCharacterId(),
		Motions:       make([]*motion.Pattern, 0, len(pb.GetMotions())),
		MoveEntries:   make([]*combat.MoveEntry, 0, len(pb.GetMoveEntries())),
		Moves:         make([]*combat.MoveData, 0, len(pb.GetMoves())),
		ReactionMoves: make(map[combat.HitReaction]string, len(pb.GetReactionMoves())),
	}

	for _, m := range pb.GetMotions() {
		p := &motion.Pattern{
			ID:             m.GetId(),
			Priority:       int(m.GetPriority()),
			Steps:          make([]motion.Step, 0, len(m.GetSteps())),
			TriggerButtons: input.ButtonMask(m.GetTriggerButtons()),
			TotalWindow:    int(m.GetTotalWindow()),
			MirrorByFacing: m.GetMirrorByFacing(),
		}
		for _, s := range m.GetSteps() {
			p.Steps = append(p.Steps, motion.Step{
				DirMask:      uint16(s.GetDirMask()),
				MaxGap:       int(s.GetMaxGap()),
				ChargeFrames: int(s.GetChargeFrames()),
			})
		}
		def.Motions = append(def.Motions, p)
	}

	for _, e := range pb.GetMoveEntries() {
		def.MoveEntries = append(def.MoveEntries, &combat.MoveEntry{
			CommandID:  e.GetCommandId(),
			Buttons:    input.ButtonMask(e.GetButtons()),
			MoveID:     e.GetMoveId(),
			Stance:     combat.Stance(e.GetStance()),
			CancelFrom: e.GetCancelFrom(),
			FeintFrom:  e.GetFeintFrom(),
			Priority:   int(e.GetPriority()),
			CancelOnly: e.GetCancelOnly(),
		})
	}

	for _, m := range pb.GetMoves() {
		def.Moves = append(def.Moves, &combat.MoveData{
			MoveID:              m.GetMoveId(),
			Startup:             int(m.GetStartup()),
			Active:              int(m.GetActive()),
			Recovery:            int(m.GetRecovery()),
			Attributes:          combat.AttackAttribute(m.GetAttributes()),
			Damage:              int(m.GetDamage()),
			HitstunFrames:       int(m.GetHitstunFrames()),
			Hitstop:             int(m.GetHitstop()),
			Reaction:            combat.HitReaction(m.GetReaction()),
			CancelFrom:          int(m.GetCancelFrom()),
			InvulnFrom:          int(m.GetInvulnFrom()),
			InvulnTo:            int(m.GetInvulnTo()),
			CatchFrom:           int(m.GetCatchFrom()),
			CatchTo:             int(m.GetCatchTo()),
			CatchMask:           combat.AttackAttribute(m.GetCatchMask()),
			CatchFollowupMoveID: m.GetCatchFollowupMoveId(),
		})
	}

	if mv := pb.GetMovement(); mv != nil {
		def.Movement = &combat.MovementConfig{
			IdleID:         mv.GetIdleId(),
			CrouchID:       mv.GetCrouchId(),
			CrouchEnterID:  mv.GetCrouchEnterId(),
			CrouchExitID:   mv.GetCrouchExitId(),
			WalkForwardID:  mv.GetWalkForwardId(),
			WalkBackwardID: mv.GetWalkBackwardId(),
			DashID:         mv.GetDashId(),
			BackDashID:     mv.GetBackDashId(),
			RunID:          mv.GetRunId(),
			JumpNeutralID:  mv.GetJumpNeutralId(),
			JumpForwardID:  mv.GetJumpForwardId(),
			JumpBackwardID: mv.GetJumpBackwardId(),
			AirDashID:      mv.GetAirDashId(),
			AirDashCount:   int(mv.GetAirDashCount()),
		}
	}

	for _, r := range pb.GetReactionMoves() {
		def.ReactionMoves[combat.HitReaction(r.GetReaction())] = r.GetMoveId()
	}

	return def
}
