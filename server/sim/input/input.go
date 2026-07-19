// Package input 是输入原语的 Go 移植，对齐客户端
// Assets/Domain/Infrastructure/Input/InputType.cs。
// ButtonMask 位值与 numpad 方向语义是协议契约（proto Input.held/pressed 就是这些位）。
package input

// ButtonMask 对齐 C# 的 [Flags] ButtonMask : byte。
type ButtonMask uint8

const (
	None ButtonMask = 0
	LP   ButtonMask = 1 << 0
	MP   ButtonMask = 1 << 1
	HP   ButtonMask = 1 << 2
	LK   ButtonMask = 1 << 3
	MK   ButtonMask = 1 << 4
	HK   ButtonMask = 1 << 5
)

// Frame 是一逻辑帧的输入快照，对齐 C# InputFrame。
type Frame struct {
	Frame     int
	Direction uint8 // numpad 1-9，5=中立
	Held      ButtonMask
	Pressed   ButtonMask
	Released  ButtonMask
}

// Mirror 把 numpad 方向左右镜像（4↔6 等），对齐 C# Numpad.Mirror。
func Mirror(dir uint8) uint8 {
	switch dir {
	case 1:
		return 3
	case 3:
		return 1
	case 4:
		return 6
	case 6:
		return 4
	case 7:
		return 9
	case 9:
		return 7
	default:
		return dir
	}
}

// FromAxes 轴向 → numpad，对齐 C# Numpad.FromAxes。
func FromAxes(dx, dy int) uint8 { return uint8((dy+1)*3 + (dx + 1) + 1) }

// Bit 方向 → 位，对齐 C# Numpad.Bit。
func Bit(dir uint8) uint16 { return uint16(1) << dir }

// Mask 多方向位掩码，对齐 C# Numpad.Mask。
func Mask(dirs ...uint8) uint16 {
	var m uint16
	for _, d := range dirs {
		m |= Bit(d)
	}
	return m
}
