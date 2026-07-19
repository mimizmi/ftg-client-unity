package combat

import "ftgserver/sim/fixed"

// ---- 枚举（序数/位值对齐 C# byte/ushort 枚举，也对齐 proto）----

// MovePhase 对齐 MoveData.cs 的 MovePhase : byte。
type MovePhase uint8

const (
	PhaseNone     MovePhase = 0
	PhaseStartup  MovePhase = 1 // 前摇：被打 = Counter Hit
	PhaseActive   MovePhase = 2 // 判定帧
	PhaseRecovery MovePhase = 3 // 后摇：确反窗口
)

// HitReaction 对齐 MoveData.cs 的 HitReaction : byte。
type HitReaction uint8

const (
	ReactionNone        HitReaction = 0
	ReactionStandLight  HitReaction = 1
	ReactionStandMedium HitReaction = 2
	ReactionStandHeavy  HitReaction = 3
	ReactionCrouchLight HitReaction = 4
	ReactionCrouchHeavy HitReaction = 5
	ReactionAirHit      HitReaction = 6
	ReactionLaunch      HitReaction = 7
	ReactionSweep       HitReaction = 8
	ReactionCrumple     HitReaction = 9
)

// AttackAttribute 对齐 MoveData.cs 的 [Flags] AttackAttribute : ushort（注意位 3=8 跳过）。
type AttackAttribute uint16

const (
	AttrNone       AttackAttribute = 0
	AttrStrike     AttackAttribute = 1 << 0 // 打击
	AttrProjectile AttackAttribute = 1 << 1 // 飞行道具
	AttrThrow      AttackAttribute = 1 << 2 // 投技
	AttrMid        AttackAttribute = 1 << 4 // 中段
	AttrLow        AttackAttribute = 1 << 5 // 下段
	AttrOverhead   AttackAttribute = 1 << 6 // 上段/Overhead
)

// BoxKind 对齐 BoxData.cs 的 BoxKind : byte。
type BoxKind uint8

const (
	BoxHit  BoxKind = 0 // 攻击框，只在 Active 帧存在
	BoxHurt BoxKind = 1 // 受击框，随姿态变化
	BoxPush BoxKind = 2 // 推挡框，防重叠
)

// MoveData 是一招的完整帧数据，对齐 C# MoveData.cs（class → Go 用 *MoveData）。
type MoveData struct {
	MoveID string

	// 帧数据（三段）
	Startup  int
	Active   int
	Recovery int

	Attributes    AttackAttribute
	Damage        int
	HitstunFrames int
	Hitstop       int
	Reaction      HitReaction
	CancelFrom    int

	BoxTracks []*BoxTrack

	// 逻辑位移增量（"面朝右"空间，index = moveFrame-1）；nil = 原地招。
	RootMotion []fixed.Vec2

	// 无敌窗口，0 = 无
	InvulnFrom int
	InvulnTo   int

	// 当身（接触反击），CatchTo > 0 即视为当身招
	CatchFrom           int
	CatchTo             int
	CatchMask           AttackAttribute
	CatchFollowupMoveID string
}

func (m *MoveData) TotalFrames() int { return m.Startup + m.Active + m.Recovery }

func (m *MoveData) IsCounterStance() bool { return m.CatchTo > 0 }

// PhaseAt 招式内帧号 → 相位。
func (m *MoveData) PhaseAt(moveFrame int) MovePhase {
	if moveFrame <= 0 {
		return PhaseNone
	}
	if moveFrame <= m.Startup {
		return PhaseStartup
	}
	if moveFrame <= m.Startup+m.Active {
		return PhaseActive
	}
	if moveFrame <= m.TotalFrames() {
		return PhaseRecovery
	}
	return PhaseNone
}

func (m *MoveData) HasBoxes(kind BoxKind) bool {
	for _, t := range m.BoxTracks {
		if t.Kind == kind {
			return true
		}
	}
	return false
}

// CollectBoxes 求本帧所有生效的指定类型的框，复用外部 slice 避免分配（对齐 C# CollectBoxes）。
func (m *MoveData) CollectBoxes(moveFrame int, kind BoxKind, results *[]Box) {
	*results = (*results)[:0]
	for _, t := range m.BoxTracks {
		if t.Kind != kind {
			continue
		}
		if b, ok := t.TryEvaluate(moveFrame); ok {
			*results = append(*results, b)
		}
	}
}
