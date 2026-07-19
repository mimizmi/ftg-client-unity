package input

// InputQuery 的 Go 移植，对齐客户端 Assets/Domain/Infrastructure/Input/InputQuery.cs。
// 三种"精确到帧"的回看：方向刚进入（拒止）、按键上升沿（拆投）、方向连续保持（蓄力）。
// 越出缓冲保留范围一律按 false（不返回脏数据），与 C# TryGet 语义一致。

// DirectionEnteredWithin 最近 frames 帧内 dirMask 方向是否发生过"刚进入"（上一帧不满足、该帧满足）。
// 用"进入"而非"存在"才能表达拒止的"轻点"语义——一直按住前不算拒止。
func DirectionEnteredWithin(buffer *Buffer, dirMask uint16, frames int, facingRight bool) bool {
	for a := range frames {
		cur, ok := buffer.TryGet(a)
		if !ok {
			return false
		}
		prev, ok := buffer.TryGet(a + 1)
		if !ok {
			return false
		}
		d0 := cur.Direction
		d1 := prev.Direction
		if !facingRight {
			d0 = Mirror(d0)
			d1 = Mirror(d1)
		}
		if (dirMask&Bit(d0)) != 0 && (dirMask&Bit(d1)) == 0 {
			return true
		}
	}
	return false
}

// ButtonPressedWithin 最近 frames 帧内是否有 buttons 中任意键的上升沿。拆投、假人反应用。
func ButtonPressedWithin(buffer *Buffer, buttons ButtonMask, frames int) bool {
	for a := range frames {
		f, ok := buffer.TryGet(a)
		if !ok {
			return false
		}
		if (f.Pressed & buttons) != 0 {
			return true
		}
	}
	return false
}

// WasHolding dirMask 方向是否已连续保持至少 frames 帧（蓄力类检查）。
func WasHolding(buffer *Buffer, dirMask uint16, frames int, facingRight bool) bool {
	for a := range frames {
		f, ok := buffer.TryGet(a)
		if !ok {
			return false
		}
		d := f.Direction
		if !facingRight {
			d = Mirror(d)
		}
		if (dirMask & Bit(d)) == 0 {
			return false
		}
	}
	return true
}
