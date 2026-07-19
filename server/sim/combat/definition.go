package combat

import (
	"ftgserver/sim/input"
	"ftgserver/sim/motion"
)

// ---- 枚举（序数对齐 C# byte 枚举与 proto）----

// Stance 对齐 MoveTable.cs 的 Stance : byte。
type Stance uint8

const (
	StanceStanding  Stance = 0
	StanceCrouching Stance = 1
	StanceAirborne  Stance = 2
)

// CancelKind 对齐 MoveTable.cs 的 CancelKind : byte。
type CancelKind uint8

const (
	CancelNone  CancelKind = 0 // 中立态出招
	CancelOnHit CancelKind = 1 // 命中取消（后摇通道）
	CancelFeint CancelKind = 2 // 变招（前摇通道）
)

// MoveEntry 对齐 MoveTable.cs 的 MoveEntry。
// C# 的 Condition（Func）不在此列——夹具契约要求条件以数据表达（见 definition.proto 头注）。
type MoveEntry struct {
	CommandID  string
	Buttons    input.ButtonMask
	MoveID     string
	Stance     Stance
	CancelFrom []string
	FeintFrom  []string
	Priority   int
	CancelOnly bool
}

// MovementConfig 对齐 MovementConfig.cs（全部是招式 Id 引用 + 空中冲刺次数）。
type MovementConfig struct {
	IdleID         string
	CrouchID       string
	CrouchEnterID  string
	CrouchExitID   string
	WalkForwardID  string
	WalkBackwardID string
	DashID         string
	BackDashID     string
	RunID          string
	JumpNeutralID  string
	JumpForwardID  string
	JumpBackwardID string
	AirDashID      string
	AirDashCount   int
}

// FighterDefinition 对齐 FighterDefinition.cs：一个角色的完整定义
// （搓招指令 + 招式表 + 招式数值 + 移动配置 + 受击映射）。
// Moves 的 BoxTracks/RootMotion 由 content.Apply 从 JSON 注入（与 C# 管线同构）。
type FighterDefinition struct {
	CharacterID   string
	Motions       []*motion.Pattern
	MoveEntries   []*MoveEntry
	Moves         []*MoveData
	Movement      *MovementConfig
	ReactionMoves map[HitReaction]string
}
