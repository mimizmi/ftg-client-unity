package content

import (
	"ftgserver/sim/combat"
	"ftgserver/sim/fixed"
	"ftgserver/sim/seat"
)

// LoadCharacter 装载一个角色的完整定义：从 protobuf 夹具读定义，再把同一份 BoxData/RootMotion
// JSON 注入招式（判定框 + 帧分割 + 无敌帧 + 位移）。返回的 def.Moves 已是可判定的运行时数据。
// 三份文件都是 C# 与 Go 共享的同一源，这是跨语言对拍成立的前提。
func LoadCharacter(defPath, boxesPath, rootMotionPath string) (*combat.FighterDefinition, error) {
	def, err := LoadDefinition(defPath)
	if err != nil {
		return nil, err
	}
	boxes, err := LoadBoxes(boxesPath)
	if err != nil {
		return nil, err
	}
	rm, err := LoadRootMotion(rootMotionPath)
	if err != nil {
		return nil, err
	}
	Apply(def.Moves, boxes, rm)
	return def, nil
}

// BuildFighter 从角色定义 + 座位装配一个 FighterState，逐字对齐 C# BattleBootstrap.BuildPlayer：
// 清座位 → 注册搓招 Pattern 到检测器 → MoveTable.AddRange(MoveEntries) →
// NewFighterState → AddMove 全部招式 → SetReactions。
// def 可被双方共享（只读配置，镜像内战两侧同一份 def 与 C# 一致）。
func BuildFighter(def *combat.FighterDefinition, s seat.Seat, spawn fixed.Vec2, name string) *combat.FighterState {
	// 座位可能是长驻对象，装配前清干净（回放座位天生全空，此步保证起点一致）
	s.Buffer().Clear()
	s.Commands().Clear()
	s.Detector().Clear()
	for _, m := range def.Motions {
		s.Detector().Add(m)
	}

	tbl := &combat.MoveTable{}
	tbl.AddRange(def.MoveEntries)

	f := combat.NewFighterState(s, tbl, def.Movement)
	f.Name = name
	f.Position = spawn
	for _, mv := range def.Moves {
		f.AddMove(mv)
	}
	f.SetReactions(def.ReactionMoves)
	return f
}
