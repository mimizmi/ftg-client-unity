package motion

import "ftgserver/sim/input"

// Library 是常用搓招模式的构造器，对齐客户端 MotionPattern.cs 的 MotionLibrary。
// 优先级默认值与 C# 一致（波动 100 / 升龙 110 / 冲刺 5），供测试与装配使用；
// 运行时角色的实际指令表来自导出的定义夹具，这里只是同构的便捷构造。

func mstep(dirs ...uint8) Step {
	return Step{DirMask: input.Mask(dirs...), MaxGap: 8}
}

// Qcf 波动 236：下(2) → 前下(3) → 前(6)。
func Qcf(id string, trigger input.ButtonMask) *Pattern {
	return &Pattern{
		ID: id, Priority: 100, TriggerButtons: trigger, TotalWindow: 22, MirrorByFacing: true,
		Steps: []Step{mstep(2), mstep(3), mstep(6)},
	}
}

// Qcb 反向波 214：下(2) → 后下(1) → 后(4)。
func Qcb(id string, trigger input.ButtonMask) *Pattern {
	return &Pattern{
		ID: id, Priority: 100, TriggerButtons: trigger, TotalWindow: 22, MirrorByFacing: true,
		Steps: []Step{mstep(2), mstep(1), mstep(4)},
	}
}

// Dp 升龙 623：前(6) → 下(2) → 前下(3)。优先级高于波动，歧义时升龙优先。
func Dp(id string, trigger input.ButtonMask) *Pattern {
	return &Pattern{
		ID: id, Priority: 110, TriggerButtons: trigger, TotalWindow: 22, MirrorByFacing: true,
		Steps: []Step{mstep(6), mstep(2), mstep(3)},
	}
}

// DashForward 双击前 66：前 → 离开前(5/2/8) → 前。指令名 "DASH_F"。
func DashForward() *Pattern {
	return &Pattern{
		ID: "DASH_F", Priority: 5, TriggerButtons: input.None, TotalWindow: 18, MirrorByFacing: true,
		Steps: []Step{mstep(6), mstep(5, 2, 8), mstep(6)},
	}
}

// DashBackward 双击后 44：后 → 离开后(5/2/8) → 后。指令名 "DASH_B"。
func DashBackward() *Pattern {
	return &Pattern{
		ID: "DASH_B", Priority: 5, TriggerButtons: input.None, TotalWindow: 18, MirrorByFacing: true,
		Steps: []Step{mstep(4), mstep(5, 2, 8), mstep(4)},
	}
}
