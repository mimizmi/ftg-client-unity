// Package statehash 是帧状态哈希的 Go 规范实现，逐字节镜像客户端
// Assets/Domain/Net/StateHasher.cs——跨语言对拍（N4）的协议契约。
//
// 算法：FNV-1a（64 位），按固定字段顺序折叠双方角色的确定性状态。字段集与顺序
// 必须与 C# StateHasher 完全一致（C# 侧 ProtoCodecTests 有断言钉死其与测试内联版一致）。
// 定点化后哈希直接吃 Fix.Raw（int32）——没有 float 位模式的平台歧义，这正是 N1/N2 的意义。
//
// 移植要点：uint 折叠按小端逐字节；字符串按 UTF-16 code unit 的 uint 值逐个折叠
// （MoveId 全 ASCII，Go rune 与 C# char 逐位等价）；nil MoveId 折叠哨兵 0xFFFFFFFF。
package statehash

import "ftgserver/sim/combat"

const (
	offsetBasis uint64 = 14695981039346656037
	prime       uint64 = 1099511628211
)

// HashState 折叠一场战斗双方角色的确定性状态为 64 位哈希（对拍契约）。
func HashState(sim *combat.BattleSimulation) uint64 {
	h := offsetBasis
	h = hashFighter(h, sim.P1)
	h = hashFighter(h, sim.P2)
	return h
}

func hashFighter(h uint64, f *combat.FighterState) uint64 {
	h = fnv(h, uint32(f.Position.X.Raw))
	h = fnv(h, uint32(f.Position.Y.Raw))
	h = fnv(h, uint32(f.Health))
	h = fnv(h, uint32(uint8(f.Status())))
	h = fnv(h, uint32(f.MoveFrame()))
	h = fnv(h, uint32(f.StunRemaining()))
	h = fnv(h, uint32(uint8(f.Movement.State())))
	h = fnv(h, uint32(f.Movement.MotionFrame()))
	if f.FacingRight {
		h = fnv(h, 1)
	} else {
		h = fnv(h, 0)
	}
	h = fnvMoveID(h, f.CurrentMove())
	return h
}

func fnv(h uint64, value uint32) uint64 {
	for i := range 4 {
		h ^= uint64(byte(value >> (uint(i) * 8)))
		h *= prime
	}
	return h
}

// fnvMoveID nil 招式折叠哨兵 0xFFFFFFFF；否则按 char 逐个折叠（对齐 C# FnvString）。
func fnvMoveID(h uint64, move *combat.MoveData) uint64 {
	if move == nil {
		return fnv(h, 0xFFFFFFFF)
	}
	for _, r := range move.MoveID {
		h = fnv(h, uint32(r))
	}
	return h
}
