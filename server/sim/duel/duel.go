// Package duel 是跨语言对拍（N4）的收官驱动器：同一份 Replay 输入夹具喂给 Go 权威模拟，
// 逐帧产出 FrameHash（= C# StateHasher 的规范折叠）。C# 侧用同一 Replay 跑 BattleSimulation
// 各吐一份 HashLog，两侧逐帧比对——首个分歧帧即定点/移植 bug 的落点。
// 这套驱动器 N5 帧同步/回滚也复用（回放即"确定的远端输入流"）。
package duel

import (
	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/combat"
	"ftgserver/sim/content"
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
	"ftgserver/sim/seat"
	"ftgserver/sim/statehash"
)

// 出生点约定：与 C# BattleBootstrap 的默认出生点一致（P1 左 P2 右）。
// 对拍两侧必须用同一出生点，否则首帧起点即分叉。
var (
	SpawnP1 = fixed.Vec2FromFloat(-1, 0)
	SpawnP2 = fixed.Vec2FromFloat(1, 0)
)

// RunReplay 用一份 Replay 输入夹具驱动 Go 模拟，逐帧产出 FrameHash（帧号 1..N，N=输入帧数）。
// p1Def/p2Def 是已注入 JSON 框/位移的角色定义（见 content.LoadCharacter）。
func RunReplay(rep *ftgv1.Replay, p1Def, p2Def *combat.FighterDefinition) []*ftgv1.FrameHash {
	p1Frames, p2Frames := splitInputs(rep.GetFrames())
	s1 := seat.NewReplaySeat(p1Frames)
	s2 := seat.NewReplaySeat(p2Frames)

	p1 := content.BuildFighter(p1Def, s1, SpawnP1, "P1")
	p2 := content.BuildFighter(p2Def, s2, SpawnP2, "P2")

	sim := combat.NewBattleSimulation(p1, p2, combat.NewCollisionResolver(),
		configFromProto(rep.GetSetup().GetConfig()))

	n := len(rep.GetFrames())
	hashes := make([]*ftgv1.FrameHash, 0, n)
	for range n {
		sim.Tick()
		hashes = append(hashes, &ftgv1.FrameHash{
			Frame: uint32(sim.CurrentFrame),
			Hash:  statehash.HashState(sim),
		})
	}
	return hashes
}

// splitInputs 把逐帧双人输入拆成 P1/P2 两条独立输入流（逐字保真 direction/held/pressed）。
func splitInputs(frames []*ftgv1.FrameInputs) (p1, p2 []input.Frame) {
	p1 = make([]input.Frame, len(frames))
	p2 = make([]input.Frame, len(frames))
	for i, fr := range frames {
		p1[i] = toFrame(fr.GetP1())
		p2[i] = toFrame(fr.GetP2())
	}
	return p1, p2
}

func toFrame(in *ftgv1.Input) input.Frame {
	if in == nil {
		return input.Frame{Direction: 5} // 缺输入 = 中立
	}
	return input.Frame{
		Direction: uint8(in.GetDirection()),
		Held:      input.ButtonMask(in.GetHeld()),
		Pressed:   input.ButtonMask(in.GetPressed()),
	}
}

func configFromProto(c *ftgv1.BattleConfig) *combat.BattleConfig {
	if c == nil {
		return combat.NewBattleConfig()
	}
	return &combat.BattleConfig{
		RoundFrames:     int(c.GetRoundFrames()),
		IntroFrames:     int(c.GetIntroFrames()),
		RoundOverFrames: int(c.GetRoundOverFrames()),
		RoundsToWin:     int(c.GetRoundsToWin()),
		MaxHealth:       int(c.GetMaxHealth()),
	}
}
