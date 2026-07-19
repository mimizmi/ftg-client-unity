package combat

import "ftgserver/sim/fixed"

// PushboxResolver 推挡解算，对齐 C# PushboxResolver.cs：防两角色重叠 + 版边约束。
// 在攻防裁决之前执行：先把位置解算干净，再用干净的位置判定命中（否则读到穿模距离）。
// 版边传导是"角落压制"的物理基础——被夹在版边和对方之间时，重叠量全推给对方。
type PushboxResolver struct {
	StageLeft     fixed.Fix
	StageRight    fixed.Fix
	MaxSeparation fixed.Fix // 留接口，暂未用

	boxesA []Box
	boxesB []Box
}

// NewPushboxResolver 默认场地 [-3, 3]，与 C# 字段初值一致。
func NewPushboxResolver() *PushboxResolver {
	return &PushboxResolver{
		StageLeft:     fixed.FromInt(-3),
		StageRight:    fixed.FromInt(3),
		MaxSeparation: fixed.FromInt(8),
	}
}

func (r *PushboxResolver) Resolve(p1, p2 *FighterState) {
	r.resolveOverlap(p1, p2)
	r.clampToStage(p1)
	r.clampToStage(p2)
	// 版边再解算一次：贴边后可能又产生重叠，需要把对方推开
	r.resolveOverlapAgainstWall(p1, p2)
}

func (r *PushboxResolver) resolveOverlap(a, b *FighterState) {
	// 空中角色不参与推挡（否则跳过对方头顶会被卡住）
	if a.Movement.IsAirborne() || b.Movement.IsAirborne() {
		return
	}
	overlap, ok := r.tryGetOverlap(a, b)
	if !ok {
		return
	}

	// 各推开一半（对称，无优先方）。×0.5 定点乘 Half，舍入向负无穷、跨语言一致。
	half := overlap.Mul(fixed.Half)
	var shift fixed.Fix
	if a.Position.X.Le(b.Position.X) {
		shift = half.Neg()
	} else {
		shift = half
	}
	a.Position = fixed.Vec2{X: a.Position.X.Add(shift), Y: a.Position.Y}
	b.Position = fixed.Vec2{X: b.Position.X.Sub(shift), Y: b.Position.Y}
}

// tryGetOverlap 取两人推挡框的最大水平重叠量。框全部来自 JSON（无硬编码"身体柱子"）。
func (r *PushboxResolver) tryGetOverlap(a, b *FighterState) (fixed.Fix, bool) {
	overlap := fixed.Zero
	a.CollectPushboxes(&r.boxesA)
	b.CollectPushboxes(&r.boxesB)
	if len(r.boxesA) == 0 || len(r.boxesB) == 0 {
		return fixed.Zero, false
	}

	for i := range r.boxesA {
		ra := r.boxesA[i].ToWorld(a.Position, a.FacingRight)
		for j := range r.boxesB {
			rb := r.boxesB[j].ToWorld(b.Position, b.FacingRight)
			if !ra.Overlaps(rb) {
				continue
			}
			o := fixed.Min(ra.XMax, rb.XMax).Sub(fixed.Max(ra.XMin, rb.XMin))
			if o.Gt(overlap) {
				overlap = o
			}
		}
	}
	return overlap, overlap.Gt(fixed.Zero)
}

func (r *PushboxResolver) clampToStage(f *FighterState) {
	f.CollectPushboxes(&r.boxesA)
	if len(r.boxesA) == 0 {
		return
	}
	// 用最宽的推挡框做边界约束
	halfW := fixed.Zero
	for _, b := range r.boxesA {
		halfW = fixed.Max(halfW, b.W.Mul(fixed.Half))
	}
	clampedX := fixed.Clamp(f.Position.X, r.StageLeft.Add(halfW), r.StageRight.Sub(halfW))
	f.Position = fixed.Vec2{X: clampedX, Y: f.Position.Y}
}

// resolveOverlapAgainstWall 版边传导：贴版边一方被夹时，重叠量全推给另一方（角落压制）。
func (r *PushboxResolver) resolveOverlapAgainstWall(a, b *FighterState) {
	if a.Movement.IsAirborne() || b.Movement.IsAirborne() {
		return
	}
	overlap, ok := r.tryGetOverlap(a, b)
	if !ok {
		return
	}

	aAtWall := r.isAtWall(a)
	bAtWall := r.isAtWall(b)

	if aAtWall && !bAtWall {
		var shift fixed.Fix
		if a.Position.X.Le(b.Position.X) {
			shift = overlap
		} else {
			shift = overlap.Neg()
		}
		b.Position = fixed.Vec2{X: b.Position.X.Add(shift), Y: b.Position.Y}
	} else if bAtWall && !aAtWall {
		var shift fixed.Fix
		if b.Position.X.Le(a.Position.X) {
			shift = overlap
		} else {
			shift = overlap.Neg()
		}
		a.Position = fixed.Vec2{X: a.Position.X.Add(shift), Y: a.Position.Y}
	}
}

func (r *PushboxResolver) isAtWall(f *FighterState) bool {
	f.CollectPushboxes(&r.boxesA)
	if len(r.boxesA) == 0 {
		return false
	}
	// 原 0.001f 浮点容差 → 定点 1/1000，语义不变
	epsilon := fixed.FromFraction(1, 1000)
	for _, b := range r.boxesA {
		rect := b.ToWorld(f.Position, f.FacingRight)
		if rect.XMin.Le(r.StageLeft.Add(epsilon)) || rect.XMax.Ge(r.StageRight.Sub(epsilon)) {
			return true
		}
	}
	return false
}
