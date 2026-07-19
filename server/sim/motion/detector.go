package motion

import (
	"sort"

	"ftgserver/sim/input"
)

// Detector 是搓招识别器，对齐客户端
// Assets/Domain/Infrastructure/Motion/MotionDetector.cs。
// Add 时按优先级【降序】排序（升龙 110 > 波动 100）：DetectAll 靠先匹配先消费触发键
// 做歧义裁决，排序方向即"歧义时升龙优先"规则本身。
// 用 SliceStable 匹配 C# List.Sort 在小列表下的插入排序（稳定）行为。
type Detector struct {
	patterns []*Pattern
}

func (d *Detector) Add(p *Pattern) {
	d.patterns = append(d.patterns, p)
	sort.SliceStable(d.patterns, func(i, j int) bool {
		return d.patterns[i].Priority > d.patterns[j].Priority
	})
}

func (d *Detector) Clear() { d.patterns = d.patterns[:0] }

// DetectAll 返回本帧识别到的全部指令。带触发键的指令按"先匹配先消费按键"裁决歧义；
// 纯方向指令（冲刺）占用单一方向槽，最多命中一条。
func (d *Detector) DetectAll(buf *input.Buffer, facingRight bool) []*Pattern {
	var results []*Pattern
	if buf.Count() == 0 {
		return results
	}
	latest := buf.Latest()
	var consumed input.ButtonMask
	directionSlotUsed := false

	for _, p := range d.patterns {
		if p.TriggerButtons != input.None {
			hit := latest.Pressed & p.TriggerButtons
			if hit == input.None {
				continue
			}
			if (hit &^ consumed) == input.None {
				continue
			}
			if !matchSteps(buf, p, facingRight) {
				continue
			}
			consumed |= hit
			results = append(results, p)
		} else {
			if directionSlotUsed {
				continue
			}
			if !lastStepJustEntered(buf, p, facingRight) {
				continue
			}
			if !matchSteps(buf, p, facingRight) {
				continue
			}
			directionSlotUsed = true
			results = append(results, p)
		}
	}
	return results
}

// lastStepJustEntered 纯方向指令的边沿判据：末步方向本帧【刚进入】（上一帧不在末步方向集）。
func lastStepJustEntered(buf *input.Buffer, p *Pattern, facingRight bool) bool {
	if buf.Count() < 2 {
		return false
	}
	cur, _ := buf.TryGet(0)
	prev, _ := buf.TryGet(1)
	lastMask := p.Steps[len(p.Steps)-1].DirMask
	d0 := normalize(cur.Direction, p, facingRight)
	d1 := normalize(prev.Direction, p, facingRight)
	return (lastMask&input.Bit(d0)) != 0 && (lastMask&input.Bit(d1)) == 0
}

// matchSteps 从最新帧逆序逐步匹配方向序列；每步在 [上一步+1, +MaxGap] 内搜索，
// 整体不超过 TotalWindow；蓄力步额外要求方向集连续保持 ChargeFrames 帧。
func matchSteps(buf *input.Buffer, p *Pattern, facingRight bool) bool {
	steps := p.Steps
	age := 0 // 0 = 触发帧
	for i := len(steps) - 1; i >= 0; i-- {
		step := steps[i]
		searchLimit := min(age+step.MaxGap, p.TotalWindow)
		found := false
		for a := age; a <= searchLimit; a++ {
			f, ok := buf.TryGet(a)
			if !ok {
				break
			}
			dir := normalize(f.Direction, p, facingRight)
			if (step.DirMask & input.Bit(dir)) == 0 {
				continue
			}
			if step.ChargeFrames > 0 && !checkCharge(buf, step, p, facingRight, a) {
				continue
			}
			found = true
			age = a + 1 // 前一步必须发生在更早的帧
			break
		}
		if !found {
			return false
		}
	}
	return true
}

func checkCharge(buf *input.Buffer, step Step, p *Pattern, facingRight bool, startAge int) bool {
	held := 0
	for a := startAge; ; a++ {
		f, ok := buf.TryGet(a)
		if !ok {
			break
		}
		dir := normalize(f.Direction, p, facingRight)
		if (step.DirMask & input.Bit(dir)) == 0 {
			break
		}
		held++
		if held >= step.ChargeFrames {
			return true
		}
	}
	return false
}

func normalize(worldDir uint8, p *Pattern, facingRight bool) uint8 {
	if facingRight || !p.MirrorByFacing {
		return worldDir
	}
	return input.Mirror(worldDir)
}
