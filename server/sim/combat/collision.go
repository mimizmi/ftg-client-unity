package combat

import (
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
)

// DefenseOutcome 对齐 C# CollisionResolver.cs 的 DefenseOutcome : byte。
type DefenseOutcome uint8

const (
	OutcomeHit           DefenseOutcome = 0 // 普通命中
	OutcomeCounterHit    DefenseOutcome = 1 // 对方出招中被打 → 硬直加成，确反奖励
	OutcomeClashed       DefenseOutcome = 2 // 拼招：双方攻击框相遇互相抵消（本作无防御的"格挡"）
	OutcomeParried       DefenseOutcome = 3 // 拒止（默认关闭）
	OutcomeCounterCaught DefenseOutcome = 4 // 被当身接住：攻守互换
	OutcomeThrown        DefenseOutcome = 5 // 被投
	OutcomeThrowTeched   DefenseOutcome = 6 // 拆投成功
)

// HitEvent 是一次攻防裁决结果，对齐 C# HitEvent。ContactPoint 由盒数据确定性算出（表现层取用）。
type HitEvent struct {
	Frame        int
	Attacker     *FighterState
	Defender     *FighterState
	Move         *MoveData
	Outcome      DefenseOutcome
	ContactPoint fixed.Vec2
}

// CollisionResolver 碰撞与攻防裁决，对齐 C# CollisionResolver。每逻辑帧在双方状态推进后调用一次。
// 本作无防御：反制规则集中在一条按优先级排列的管线里——
// ⓪拼招 → ①无敌 → ②当身 → ③投/拆投 → ④拒止(默认关) → ⑤命中(含 CH)。顺序即规则。
type CollisionResolver struct {
	// 拼招（无防御设计的核心）
	ClashEnabled bool
	ClashHitstop int

	// 拒止（默认关，与"无防御"设计冲突）
	ParryEnabled          bool
	ParryWindow           int
	ParryForward          bool
	ThrowTechWindow       int
	CounterHitDamageScale fixed.Fix
	CounterHitBonusStun   int

	// 顿帧
	HitHitstop      int
	CounterHitBonus int
	ParryHitstop    int

	// 复用 slice 避免每帧分配（对齐 C# 的 readonly List 字段）
	attackBoxes []Box
	defendBoxes []Box
}

// NewCollisionResolver 用与 C# 字段初值一致的默认参数构造。
func NewCollisionResolver() *CollisionResolver {
	return &CollisionResolver{
		ClashEnabled:          true,
		ClashHitstop:          16,
		ParryEnabled:          false,
		ParryWindow:           8,
		ParryForward:          true,
		ThrowTechWindow:       5,
		CounterHitDamageScale: fixed.FromFraction(6, 5), // ×1.2
		CounterHitBonusStun:   8,
		HitHitstop:            8,
		CounterHitBonus:       4,
		ParryHitstop:          16,
	}
}

// Resolve 裁决本帧攻防，把事件追加到 results（先清空）。
func (r *CollisionResolver) Resolve(frame int, p1, p2 *FighterState, results *[]*HitEvent) {
	*results = (*results)[:0]

	// ⓪ 拼招：双方攻击框相遇 → 两招互抵 + 双方定格 + 取消窗同开。本帧不再裁定命中。
	if r.ClashEnabled {
		if clashPoint, ok := r.testClash(p1, p2); ok {
			clash := &HitEvent{
				Frame: frame, Attacker: p1, Defender: p2,
				Move: p1.CurrentMove(), Outcome: OutcomeClashed, ContactPoint: clashPoint,
			}
			*results = append(*results, clash)
			r.apply(clash)
			return
		}
	}

	// 先对称检测再统一施加：同帧互中 = 相杀，两边都吃结果
	p1Contact, p1HitsP2 := r.testOverlap(p1, p2)
	p2Contact, p2HitsP1 := r.testOverlap(p2, p1)

	if p1HitsP2 {
		*results = append(*results, r.judge(frame, p1, p2, p1Contact))
	}
	if p2HitsP1 {
		*results = append(*results, r.judge(frame, p2, p1, p2Contact))
	}

	for _, ev := range *results {
		r.apply(ev)
	}
}

// testClash 拼招检测：双方同处判定期（Active 且未命中）且打击技攻击框相互重叠（投不参与）。
func (r *CollisionResolver) testClash(a, b *FighterState) (fixed.Vec2, bool) {
	if !a.CanMoveConnect() || !b.CanMoveConnect() {
		return fixed.Vec2Zero, false
	}
	if (a.CurrentMove().Attributes & AttrStrike) == 0 {
		return fixed.Vec2Zero, false
	}
	if (b.CurrentMove().Attributes & AttrStrike) == 0 {
		return fixed.Vec2Zero, false
	}

	a.CurrentMove().CollectBoxes(a.MoveFrame(), BoxHit, &r.attackBoxes)
	if len(r.attackBoxes) == 0 {
		return fixed.Vec2Zero, false
	}
	b.CurrentMove().CollectBoxes(b.MoveFrame(), BoxHit, &r.defendBoxes)
	if len(r.defendBoxes) == 0 {
		return fixed.Vec2Zero, false
	}

	for i := range r.attackBoxes {
		ra := r.attackBoxes[i].ToWorld(a.Position, a.FacingRight)
		for j := range r.defendBoxes {
			rb := r.defendBoxes[j].ToWorld(b.Position, b.FacingRight)
			if ra.Overlaps(rb) {
				return intersectionCenter(ra, rb), true
			}
		}
	}
	return fixed.Vec2Zero, false
}

// testOverlap 攻击框 vs 受击框。受击框随姿态变化（蹲下变矮）——上段打空、下段命中的机制基础。
func (r *CollisionResolver) testOverlap(attacker, defender *FighterState) (fixed.Vec2, bool) {
	if !attacker.CanMoveConnect() {
		return fixed.Vec2Zero, false
	}
	if defender.IsInvulnerable() {
		return fixed.Vec2Zero, false // ① 无敌：升龙无敌帧穿招
	}

	attacker.CurrentMove().CollectBoxes(attacker.MoveFrame(), BoxHit, &r.attackBoxes)
	if len(r.attackBoxes) == 0 {
		return fixed.Vec2Zero, false
	}
	defender.CollectHurtboxes(&r.defendBoxes)
	if len(r.defendBoxes) == 0 {
		return fixed.Vec2Zero, false
	}

	for i := range r.attackBoxes {
		hit := r.attackBoxes[i].ToWorld(attacker.Position, attacker.FacingRight)
		for j := range r.defendBoxes {
			hurt := r.defendBoxes[j].ToWorld(defender.Position, defender.FacingRight)
			if hit.Overlaps(hurt) {
				return intersectionCenter(hit, hurt), true
			}
		}
	}
	return fixed.Vec2Zero, false
}

// intersectionCenter 两矩形交集中心（仅在已确认 Overlaps 后调用）。
func intersectionCenter(a, b fixed.Rect) fixed.Vec2 {
	xMin := fixed.Max(a.XMin, b.XMin)
	xMax := fixed.Min(a.XMax, b.XMax)
	yMin := fixed.Max(a.YMin, b.YMin)
	yMax := fixed.Min(a.YMax, b.YMax)
	return fixed.Vec2{
		X: xMin.Add(xMax).Mul(fixed.Half),
		Y: yMin.Add(yMax).Mul(fixed.Half),
	}
}

func (r *CollisionResolver) judge(frame int, attacker, defender *FighterState, contact fixed.Vec2) *HitEvent {
	move := attacker.CurrentMove()
	attacker.MarkMoveConnected()

	ev := &HitEvent{
		Frame: frame, Attacker: attacker, Defender: defender,
		Move: move, ContactPoint: contact,
	}

	// ② 当身：守方处接触窗口，且来击类型在可接范围内
	if defender.CounterCatchActive() &&
		(move.Attributes&defender.CurrentMove().CatchMask) != 0 {
		ev.Outcome = OutcomeCounterCaught
		return ev
	}

	// ③ 投：不可防不可拒止，但可拆——回看被投方最近 N 帧是否按过投
	if (move.Attributes & AttrThrow) != 0 {
		teched := defender.Status() == StatusNeutral &&
			input.ButtonPressedWithin(defender.InputHistory(), input.LP|input.LK, r.ThrowTechWindow)
		if teched {
			ev.Outcome = OutcomeThrowTeched
		} else {
			ev.Outcome = OutcomeThrown
		}
		return ev
	}

	// ④ 拒止：碰撞帧回看守方自己的输入缓冲（默认关）
	if r.ParryEnabled &&
		defender.Status() == StatusNeutral &&
		(move.Attributes&(AttrStrike|AttrProjectile)) != 0 {
		var parryDir uint16
		if r.ParryForward {
			parryDir = input.Mask(6)
		} else {
			parryDir = input.Mask(4)
		}
		if input.DirectionEnteredWithin(defender.InputHistory(), parryDir, r.ParryWindow, defender.FacingRight) {
			ev.Outcome = OutcomeParried
			return ev
		}
	}

	// ⑤ 命中。CH 判定 = 守方正处于自己招式的前摇/后摇（读状态层，不读按键）
	counterHit := (defender.Status() == StatusAttacking &&
		(defender.Phase() == PhaseStartup || defender.Phase() == PhaseRecovery)) ||
		defender.Status() == StatusCounterStance
	if counterHit {
		ev.Outcome = OutcomeCounterHit
	} else {
		ev.Outcome = OutcomeHit
	}
	return ev
}

func (r *CollisionResolver) apply(ev *HitEvent) {
	move := ev.Move
	r.applyHitstop(ev) // 攻防双方同时定格；投/拆投/当身 v1 不加
	switch ev.Outcome {
	case OutcomeHit:
		ev.Defender.ApplyHit(move.Damage, move.HitstunFrames, move.Reaction)

	case OutcomeCounterHit:
		scaled := fixed.FromInt(int32(move.Damage)).Mul(r.CounterHitDamageScale).RoundToInt()
		ev.Defender.ApplyHit(int(scaled), move.HitstunFrames+r.CounterHitBonusStun, move.Reaction)

	case OutcomeClashed:
		// 两招互抵（本招不再能命中），同时这是命中取消的门闩——双方都可立刻取消续招。
		ev.Attacker.MarkMoveConnected()
		ev.Defender.MarkMoveConnected()

	case OutcomeParried:
		// 守方无伤无硬直，攻方照常收招 → 天然巨大确反窗口

	case OutcomeCounterCaught:
		// 攻守互换：攻方吃大硬直，守方自动转入反击招
		ev.Attacker.ApplyHit(0, 30, ReactionNone)
		if ev.Defender.CurrentMove() != nil && ev.Defender.CurrentMove().CatchFollowupMoveID != "" {
			ev.Defender.StartMove(ev.Defender.CurrentMove().CatchFollowupMoveID)
		}

	case OutcomeThrown:
		ev.Defender.ApplyHit(move.Damage, move.HitstunFrames, move.Reaction)

	case OutcomeThrowTeched:
		ev.Attacker.ApplyBlockstun(12)
		ev.Defender.ApplyBlockstun(12)
	}
}

// applyHitstop 按结果给攻防双方置入顿帧。命中/CH 可被 MoveData.Hitstop 覆盖；投/拆投/当身 v1 不加。
func (r *CollisionResolver) applyHitstop(ev *HitEvent) {
	hit := r.HitHitstop
	if ev.Move.Hitstop > 0 {
		hit = ev.Move.Hitstop
	}
	var frames int
	switch ev.Outcome {
	case OutcomeHit:
		frames = hit
	case OutcomeCounterHit:
		frames = hit + r.CounterHitBonus
	case OutcomeClashed:
		frames = r.ClashHitstop
	case OutcomeParried:
		frames = r.ParryHitstop
	default:
		return // Thrown / ThrowTeched / CounterCaught
	}
	ev.Attacker.ApplyHitstop(frames)
	ev.Defender.ApplyHitstop(frames)
}
