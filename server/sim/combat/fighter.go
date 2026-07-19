package combat

import (
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
)

// FighterStatus 对齐 C# FighterState.cs 的 FighterStatus : byte（序数 0-4 也对齐 proto）。
type FighterStatus uint8

const (
	StatusNeutral       FighterStatus = 0
	StatusAttacking     FighterStatus = 1
	StatusCounterStance FighterStatus = 2
	StatusHitstun       FighterStatus = 3
	StatusBlockstun     FighterStatus = 4
)

// Seat 是 FighterState 对输入座位的视图：读缓冲/指令 + 朝向回写 + 每帧采样。
// 比 MoveInput 宽（多了 FacingRight/ManualTick，供 BattleSimulation 驱动），
// *seat.ScriptedSeat 结构化满足即可注入——combat 无需 import seat。
type Seat interface {
	Buffer() *input.Buffer
	Commands() *input.CommandQueue
	FacingRight() bool
	SetFacingRight(v bool)
	ManualTick()
}

// FighterSnapshot 是某帧一个角色的只读快照，对齐 C# FighterSnapshot。
type FighterSnapshot struct {
	Frame       int
	Position    fixed.Vec2
	FacingRight bool
	Status      FighterStatus
	MoveID      string
	MoveFrame   int
	Phase       MovePhase
}

// FighterState 是单个角色的全部战斗状态与每帧推进，对齐 C# FighterState.cs。
// 招式机（本类）与移动机（MovementController）并列，由本类的 Tick 调度。
// 位置是模拟内唯一权威（定点），表现层经 ToFloat 只读取用。
type FighterState struct {
	Name        string
	Position    fixed.Vec2 // 模拟权威位置（定点）
	FacingRight bool
	Health      int

	input     Seat
	moveTable *MoveTable
	moves     map[string]*MoveData

	Movement *MovementController

	status          FighterStatus
	currentMove     *MoveData
	moveFrame       int
	currentReaction HitReaction

	stunRemaining int
	stunTotal     int  // 本次硬直总帧数（表现层硬同步受击动画用）
	moveConnected bool // 当前招是否已命中过（防多段重复判定）

	// 普通技预输入缓冲（让裸按键享受与搓招同等的预输入窗口）
	NormalBufferFrames int
	bufferedPress      input.ButtonMask
	bufferedPressFrame int
	// 模拟帧：顿帧中不推进，使输入缓冲"看穿"顿帧不老化
	simFrame int

	// 受击招式（击退位移载体）：受击类别 → MoveId 映射 + 本次命中的具体受击招式
	reactionMoveIds map[HitReaction]string
	reactionMove    *MoveData

	// 顿帧（命中定格冻结模拟推进的帧数）
	hitstop int

	// 出招瞬间回调（对手反应系统订阅；headless 可留 nil）。对齐 C# MoveStarted 事件。
	OnMoveStarted func(f *FighterState, move *MoveData)
}

func NewFighterState(in Seat, moveTable *MoveTable, movementConfig *MovementConfig) *FighterState {
	f := &FighterState{
		input:              in,
		moveTable:          moveTable,
		moves:              make(map[string]*MoveData),
		FacingRight:        true,
		Health:             1000,
		status:             StatusNeutral,
		NormalBufferFrames: 4,
	}
	f.Movement = NewMovementController(movementConfig, in, func(id string) *MoveData {
		return f.moves[id]
	})
	return f
}

// ---- 只读视图 ----

func (f *FighterState) Status() FighterStatus         { return f.status }
func (f *FighterState) CurrentMove() *MoveData        { return f.currentMove }
func (f *FighterState) MoveFrame() int                { return f.moveFrame }
func (f *FighterState) StunRemaining() int            { return f.stunRemaining }
func (f *FighterState) StunTotalFrames() int          { return f.stunTotal }
func (f *FighterState) CurrentReaction() HitReaction  { return f.currentReaction }
func (f *FighterState) InHitstop() bool               { return f.hitstop > 0 }
func (f *FighterState) InputHistory() *input.Buffer   { return f.input.Buffer() }
func (f *FighterState) Commands() *input.CommandQueue { return f.input.Commands() }
func (f *FighterState) Seat() Seat                    { return f.input }

// CurrentStance 当前姿态。招式表用它把同一输入解析成 5LP/2LP/j.LP。空中由移动机决定，
// 否则由（按朝向归一化的）方向键推断。
func (f *FighterState) CurrentStance() Stance {
	if f.Movement.IsAirborne() {
		return StanceAirborne
	}
	dir := f.input.Buffer().Latest().Direction
	if !f.FacingRight {
		dir = input.Mirror(dir)
	}
	if dir == 1 || dir == 2 || dir == 3 {
		return StanceCrouching
	}
	return StanceStanding
}

// Phase 招式相位（仅出招/当身态有意义）。
func (f *FighterState) Phase() MovePhase {
	if (f.status == StatusAttacking || f.status == StatusCounterStance) && f.currentMove != nil {
		return f.currentMove.PhaseAt(f.moveFrame)
	}
	return PhaseNone
}

// Actionable 可行动 = 中立态。
func (f *FighterState) Actionable() bool { return f.status == StatusNeutral }

// IsInvulnerable 无敌。两来源：招式自带无敌帧（升龙）+ 移动机后跃步无敌帧。
func (f *FighterState) IsInvulnerable() bool {
	moveInv := f.currentMove != nil && f.currentMove.InvulnTo > 0 &&
		f.moveFrame >= f.currentMove.InvulnFrom && f.moveFrame <= f.currentMove.InvulnTo &&
		(f.status == StatusAttacking || f.status == StatusCounterStance)
	return moveInv || f.Movement.IsInvulnerable()
}

// CounterCatchActive 当身接触窗口内。
func (f *FighterState) CounterCatchActive() bool {
	return f.status == StatusCounterStance && f.currentMove != nil &&
		f.moveFrame >= f.currentMove.CatchFrom && f.moveFrame <= f.currentMove.CatchTo
}

// CanMoveConnect 当前招能否命中（判定期 + 有攻击框 + 本招未命中过）。
func (f *FighterState) CanMoveConnect() bool {
	return f.status == StatusAttacking && f.currentMove != nil &&
		f.currentMove.HasBoxes(BoxHit) && !f.moveConnected
}

// CollectHurtboxes 收集本帧受击框（招式→移动→待机 回退链）。缺框则空（C# 侧此处告警）。
func (f *FighterState) CollectHurtboxes(results *[]Box) {
	if f.tryCollect(BoxHurt, results) {
		return
	}
	*results = (*results)[:0]
}

// CollectPushboxes 收集本帧推挡框（同一回退链）。缺框 = 不参与推挡（合法）。
func (f *FighterState) CollectPushboxes(results *[]Box) {
	if f.tryCollect(BoxPush, results) {
		return
	}
	*results = (*results)[:0]
}

func (f *FighterState) tryCollect(kind BoxKind, results *[]Box) bool {
	if f.currentMove != nil &&
		(f.status == StatusAttacking || f.status == StatusCounterStance) &&
		f.currentMove.HasBoxes(kind) {
		f.currentMove.CollectBoxes(f.moveFrame, kind, results)
		if len(*results) > 0 {
			return true
		}
	}

	motion := f.Movement.CurrentMotion()
	if motion != nil && motion.HasBoxes(kind) {
		motion.CollectBoxes(f.Movement.MotionFrame(), kind, results)
		if len(*results) > 0 {
			return true
		}
	}

	idle := f.Movement.IdleMove()
	if idle != nil && idle.HasBoxes(kind) {
		idle.CollectBoxes(1, kind, results)
		if len(*results) > 0 {
			return true
		}
	}
	return false
}

func (f *FighterState) Snapshot(frame int) FighterSnapshot {
	var moveID string
	if f.currentMove != nil {
		moveID = f.currentMove.MoveID
	}
	return FighterSnapshot{
		Frame: frame, Position: f.Position, FacingRight: f.FacingRight,
		Status: f.status, MoveID: moveID, MoveFrame: f.moveFrame, Phase: f.Phase(),
	}
}

func (f *FighterState) AddMove(move *MoveData) { f.moves[move.MoveID] = move }

// SetReactions 注入"受击类别 → 受击招式 MoveId"映射（受击招式本身在 moves 里）。
func (f *FighterState) SetReactions(m map[HitReaction]string) { f.reactionMoveIds = m }

// ===================== 每帧推进 =====================

// Tick 由 BattleSimulation 每逻辑帧调用（输入采样之后、碰撞裁决之前）。
func (f *FighterState) Tick(frame int) {
	// 每帧记录最近按键下降沿（即使不可行动——锁定/顿帧里按的键正是要缓冲的对象）。
	pressedNow := f.input.Buffer().Latest().Pressed
	if pressedNow != input.None {
		f.bufferedPress = pressedNow
		f.bufferedPressFrame = f.simFrame
	}

	// 顿帧：冻结模拟推进（招式帧/移动/硬直/受击位移全暂停）。simFrame 不推进 → 预输入不老化。
	if f.hitstop > 0 {
		f.hitstop--
		return
	}

	f.simFrame++
	f.tickCombat()

	// 移动只在可行动时生效；出招/硬直中移动层自动归零（空中除外）。
	f.Movement.Tick(f.Actionable(), f.FacingRight, &f.Position)
}

func (f *FighterState) tickCombat() {
	switch f.status {
	case StatusHitstun, StatusBlockstun:
		f.applyReactionRootMotion() // 受击位移（击退）
		f.stunRemaining--
		if f.stunRemaining > 0 {
			return
		}
		f.status = StatusNeutral
		f.reactionMove = nil
		f.tickNeutral() // 硬直结束当帧即可行动 → reversal

	case StatusAttacking, StatusCounterStance:
		f.moveFrame++
		if f.moveFrame <= f.currentMove.TotalFrames() {
			f.applyRootMotion() // 逻辑位移与判定同帧、同确定性
			f.tryCancel()       // 命中后可取消 → 连招
			return
		}
		f.endMove()
		f.tickNeutral() // 收招当帧即可行动

	case StatusNeutral:
		f.tickNeutral()
	}
}

// tickNeutral 中立态出招判定。移动机锁死的帧（起跳预备/冲刺起步/落地硬直）不能出招。
func (f *FighterState) tickNeutral() {
	if f.Movement.CanAct() {
		f.tryAct("", CancelNone)
	}
}

// tryCancel 招式进行中的取消判定：前摇=变招（无需命中，须变不同招），Active/后摇=命中取消（连招）。
func (f *FighterState) tryCancel() {
	if f.Phase() == PhaseStartup {
		f.tryAct(f.currentMove.MoveID, CancelFeint)
		return
	}
	if !f.moveConnected {
		return // 放空不给取消
	}
	if f.currentMove.CancelFrom > 0 && f.moveFrame < f.currentMove.CancelFrom {
		return
	}
	f.tryAct(f.currentMove.MoveID, CancelOnHit)
}

func (f *FighterState) tryAct(cancelSource string, cancelKind CancelKind) {
	latest := f.input.Buffer().Latest()
	stance := f.CurrentStance()

	// ① 搓招指令（队列带优先级与预输入窗口）→ 招式表解析
	resolvable := func(c *input.DetectedCommand) bool {
		return f.moveTable.ResolveCommand(c.ID, latest.Pressed, stance, cancelSource, cancelKind) != ""
	}
	if cmd, ok := f.input.Commands().TryPeek(resolvable); ok {
		moveID := f.moveTable.ResolveCommand(cmd.ID, latest.Pressed, stance, cancelSource, cancelKind)
		id := cmd.ID
		f.input.Commands().TryConsume(func(c *input.DetectedCommand) bool { return c.ID == id })
		f.StartMove(moveID)
		return
	}

	// ② 组合键投（LP+LK 同按）。只能中立态出，不从取消/变招出
	if cancelKind == CancelNone {
		throwInput := ((latest.Pressed&input.LP) != 0 && (latest.Held&input.LK) != 0) ||
			((latest.Pressed&input.LK) != 0 && (latest.Held&input.LP) != 0)
		if throwInput {
			if _, ok := f.moves["THROW"]; ok {
				f.StartMove("THROW")
				return
			}
		}
	}

	// ③ 普通技：裸按键 → 招式表解析（姿态决定 5LP/2LP/j.LP）。当帧无新按下时回看预输入缓冲。
	press := latest.Pressed
	if press == input.None &&
		f.bufferedPress != input.None &&
		f.simFrame-f.bufferedPressFrame <= f.NormalBufferFrames {
		press = f.bufferedPress
	}
	if press != input.None {
		moveID := f.moveTable.ResolveButton(press, stance, cancelSource, cancelKind)
		if moveID != "" {
			f.bufferedPress = input.None // 兑现即消费，避免一次按下连出两招
			f.StartMove(moveID)
		}
	}
}

func (f *FighterState) StartMove(moveID string) bool {
	move, ok := f.moves[moveID]
	if !ok {
		return false
	}
	f.currentMove = move
	f.moveFrame = 1
	f.moveConnected = false
	if move.IsCounterStance() {
		f.status = StatusCounterStance
	} else {
		f.status = StatusAttacking
	}
	if f.OnMoveStarted != nil {
		f.OnMoveStarted(f, move)
	}
	f.applyRootMotion() // 出招当帧（第 1 帧）位移
	return true
}

// applyRootMotion 消费 CurrentMove.RootMotion 当前帧位移增量（面朝右空间，按朝向镜像 X）。
func (f *FighterState) applyRootMotion() {
	if f.currentMove == nil {
		return
	}
	motion := f.currentMove.RootMotion
	if motion == nil {
		return
	}
	index := f.moveFrame - 1
	if index < 0 || index >= len(motion) {
		return
	}
	delta := motion[index]
	if !f.FacingRight {
		delta = delta.MirrorX()
	}
	f.Position = f.Position.Add(delta)
}

func (f *FighterState) endMove() {
	f.currentMove = nil
	f.moveFrame = 0
	f.moveConnected = false
	f.status = StatusNeutral
}

// ResetForRound 回合级重置：归位满血、回合内状态清零。simFrame 不重置（跨回合单调递增无害）。
func (f *FighterState) ResetForRound(spawn fixed.Vec2, health int) {
	f.Position = spawn
	f.Health = health
	f.endMove()
	f.currentReaction = ReactionNone
	f.reactionMove = nil
	f.stunRemaining = 0
	f.stunTotal = 0
	f.hitstop = 0
	f.bufferedPress = input.None
	f.bufferedPressFrame = 0
	f.input.Commands().Clear() // 上回合搓一半的指令不许穿越回合
	f.Movement.HardReset()
}

// ---- 由 CollisionResolver 调用的结果施加 ----

func (f *FighterState) MarkMoveConnected() { f.moveConnected = true }

// ApplyHitstop 置入顿帧，取 max（相杀/多段同帧不互相缩短彼此定格）。
func (f *FighterState) ApplyHitstop(frames int) {
	if frames > f.hitstop {
		f.hitstop = frames
	}
}

// ApplyHit 施加命中：扣血、解析受击类别与受击招式、打断招式进入硬直、清预输入。
func (f *FighterState) ApplyHit(damage, hitstunFrames int, reaction HitReaction) {
	f.Health -= damage
	// 受击类别必须在打断移动之前解析（空中/蹲姿依赖当前移动与输入状态）
	f.currentReaction = f.resolveReaction(reaction)
	f.reactionMove = f.resolveReactionMove(f.currentReaction)
	f.endMove()
	f.status = StatusHitstun
	f.stunRemaining = hitstunFrames
	f.stunTotal = hitstunFrames
	f.input.Commands().Clear() // 受击前搓好的招作废；硬直中新搓的重新入队 = reversal
	f.bufferedPress = input.None

	// 受击打断移动，但跳跃必须播完整条抛物线（判据与 MovementController.Tick 的 !IsJumping 一致）
	if !f.Movement.IsJumping() {
		f.Movement.Reset()
	}
}

// ApplyBlockstun 施加防御硬直（拆投后的小硬直等）。无受击招式。
func (f *FighterState) ApplyBlockstun(frames int) {
	f.endMove()
	f.status = StatusBlockstun
	f.stunRemaining = frames
	f.stunTotal = frames
	f.reactionMove = nil
}

func (f *FighterState) resolveReactionMove(reaction HitReaction) *MoveData {
	if reaction == ReactionNone || f.reactionMoveIds == nil {
		return nil
	}
	id, ok := f.reactionMoveIds[reaction]
	if !ok {
		return nil
	}
	m, ok := f.moves[id]
	if !ok {
		return nil
	}
	return m
}

// applyReactionRootMotion 结算受击招式当前帧的击退位移（与 applyRootMotion 同构，index=已历硬直帧）。
func (f *FighterState) applyReactionRootMotion() {
	if f.reactionMove == nil {
		return
	}
	motion := f.reactionMove.RootMotion
	if motion == nil {
		return
	}
	// 空中被击时位置由跳跃抛物线独占，受击位移不叠加（否则双重位移）。
	if f.Movement.IsJumping() {
		return
	}
	index := f.stunTotal - f.stunRemaining // 0 起：首个硬直 tick 结算第 1 帧位移
	if index < 0 || index >= len(motion) {
		return
	}
	delta := motion[index]
	if !f.FacingRight {
		delta = delta.MirrorX()
	}
	f.Position = f.Position.Add(delta)
}

// resolveReaction 把攻击声明的基础受击类别按挨打者姿态解析：空中→AirHit；蹲姿把站立档降为蹲姿档。
func (f *FighterState) resolveReaction(attackReaction HitReaction) HitReaction {
	if attackReaction == ReactionNone {
		return ReactionNone
	}
	stance := f.CurrentStance()
	if stance == StanceAirborne {
		return ReactionAirHit
	}
	if stance == StanceCrouching {
		switch attackReaction {
		case ReactionStandLight:
			return ReactionCrouchLight
		case ReactionStandMedium:
			return ReactionCrouchHeavy // 未细分蹲中档则并入蹲重
		case ReactionStandHeavy:
			return ReactionCrouchHeavy
		}
	}
	return attackReaction
}
