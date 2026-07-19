// Package motion 是搓招指令识别的 Go 移植，对齐客户端
// Assets/Domain/Infrastructure/Motion。本文件只有数据结构（识别模式）；
// 检测器（MotionDetector）随输入管线移植时补充。
package motion

import "ftgserver/sim/input"

// Step 对齐 MotionPattern.cs 的 MotionStep。
type Step struct {
	DirMask      uint16 // numpad 方向位掩码
	MaxGap       int
	ChargeFrames int
}

// Pattern 对齐 MotionPattern.cs 的 MotionPattern。
type Pattern struct {
	ID             string
	Priority       int
	Steps          []Step
	TriggerButtons input.ButtonMask
	TotalWindow    int
	MirrorByFacing bool
}
