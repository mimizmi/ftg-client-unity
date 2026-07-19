package combat_test

import (
	"testing"

	"ftgserver/sim/combat"
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
	"ftgserver/sim/seat"
	"ftgserver/sim/statehash"
)

// 战斗整合的 Go 镜像：推挡分离 / 命中扣血硬直 / 连段计数 / 整局逐帧 HashState 双跑一致。
// 最后一条是 N4 跨语言对拍的本地自证——同一份输入喂两套模拟，逐帧规范哈希必须逐位相同。

// --- 夹具 ---

// constTrack 造一条整段恒定的判定框轨道（首尾同值关键帧 → 插值恒定）。
func constTrack(kind combat.BoxKind, from, to int, x, y, w, h float32) *combat.BoxTrack {
	return &combat.BoxTrack{
		Kind: kind, FromFrame: from, ToFrame: to,
		Keys: []combat.BoxKeyframe{
			{Frame: from, X: x, Y: y, W: w, H: h},
			{Frame: to, X: x, Y: y, W: w, H: h},
		},
	}
}

// idleMove 待机：有身体柱子（hurt + push，居中 0.8×2），无位移无攻击框。
func idleMove() *combat.MoveData {
	return &combat.MoveData{
		MoveID: "idle", Startup: 0, Active: 1, Recovery: 0,
		BoxTracks: []*combat.BoxTrack{
			constTrack(combat.BoxHurt, 1, 1, 0, 1, 0.8, 2),
			constTrack(combat.BoxPush, 1, 1, 0, 1, 0.8, 2),
		},
	}
}

// jab 5LP：前摇 3 / 判定 3（帧 4-6 出攻击框）/ 后摇 6。攻击框在身前，命中 30 伤 16 硬直。
func jab() *combat.MoveData {
	return &combat.MoveData{
		MoveID: "5LP", Startup: 3, Active: 3, Recovery: 6,
		Attributes: combat.AttrStrike, Damage: 30,
		HitstunFrames: 16, Reaction: combat.ReactionStandLight,
		BoxTracks: []*combat.BoxTrack{
			constTrack(combat.BoxHit, 4, 6, 0.6, 1.2, 0.6, 0.3),
			constTrack(combat.BoxHurt, 1, 12, 0, 1, 0.8, 2),
			constTrack(combat.BoxPush, 1, 12, 0, 1, 0.8, 2),
		},
	}
}

func movementConfig() *combat.MovementConfig {
	return &combat.MovementConfig{IdleID: "idle", AirDashCount: 1}
}

// buildFighter 组装一个带待机+5LP、招式表认 LP→5LP 的角色。座位经 s 出参回传给调用方。
func buildFighter(s *combat.Seat, script func(int) seat.ScriptedInput, x float32) *combat.FighterState {
	sc := seat.NewScriptedSeat(script)
	*s = sc
	tbl := &combat.MoveTable{}
	tbl.Add(&combat.MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10})
	f := combat.NewFighterState(sc, tbl, movementConfig())
	f.AddMove(idleMove())
	f.AddMove(jab())
	f.Position = fixed.Vec2FromFloat(x, 0)
	return f
}

func neutral(int) seat.ScriptedInput { return seat.ScriptedInput{Direction: 5} }

// buildSim 造一整场：两 fighter + 碰撞裁决 + 战斗模拟。
func buildSim(p1Script, p2Script func(int) seat.ScriptedInput, x1, x2 float32) *combat.BattleSimulation {
	var s1, s2 combat.Seat
	p1 := buildFighter(&s1, p1Script, x1)
	p2 := buildFighter(&s2, p2Script, x2)
	return combat.NewBattleSimulation(p1, p2, combat.NewCollisionResolver(), combat.NewBattleConfig())
}

// --- 测试 ---

func TestPushbox_SeparatesOverlappingFighters(t *testing.T) {
	// 两人重叠在原点，一帧推挡后应对称分开、不再重叠
	sim := buildSim(neutral, neutral, 0, 0)
	sim.Tick()

	if !(sim.P1.Position.X.Raw < 0 && sim.P2.Position.X.Raw > 0) {
		t.Fatalf("推挡应把两人对称推开：P1.X=%d P2.X=%d", sim.P1.Position.X.Raw, sim.P2.Position.X.Raw)
	}
	if sim.P1.Position.X.Raw != -sim.P2.Position.X.Raw {
		t.Errorf("应对称：P1.X=%d 期望 -P2.X=%d", sim.P1.Position.X.Raw, -sim.P2.Position.X.Raw)
	}
}

func TestHit_ReducesHealthAndStuns(t *testing.T) {
	// P1 按住 LP（点招），P2 中立挨打。攻击框在帧 4-6，命中扣 30 血、P2 进硬直。
	holdLP := func(int) seat.ScriptedInput { return seat.ScriptedInput{Direction: 5, Held: input.LP} }
	sim := buildSim(holdLP, neutral, -0.5, 0.5)

	sawHitstun := false
	for range 20 {
		sim.Tick()
		if sim.P2.Status() == combat.StatusHitstun {
			sawHitstun = true
		}
	}

	if sim.P2.Health != 970 {
		t.Errorf("P2 血量=%d 期望 970（1000-30）", sim.P2.Health)
	}
	if !sawHitstun {
		t.Error("P2 应在某帧进入受击硬直")
	}
	if sim.P1.Health != 1000 {
		t.Errorf("P1 不该掉血=%d", sim.P1.Health)
	}
}

func TestHit_RegistersComboCountOne(t *testing.T) {
	holdLP := func(int) seat.ScriptedInput { return seat.ScriptedInput{Direction: 5, Held: input.LP} }
	sim := buildSim(holdLP, neutral, -0.5, 0.5)

	maxCombo := 0
	for range 20 {
		sim.Tick()
		if sim.P1ComboHits > maxCombo {
			maxCombo = sim.P1ComboHits
		}
	}
	if maxCombo != 1 {
		t.Errorf("单段命中 P1ComboHits 峰值=%d 期望 1", maxCombo)
	}
}

func TestDeterminism_NeutralHashTrace(t *testing.T) {
	trace := func() []uint64 {
		sim := buildSim(neutral, neutral, -0.5, 0.5)
		out := make([]uint64, 0, 60)
		for range 60 {
			sim.Tick()
			out = append(out, statehash.HashState(sim))
		}
		return out
	}
	assertSameTrace(t, trace(), trace())
}

func TestDeterminism_FullScenarioHashTrace(t *testing.T) {
	// P1 走近再出拳、P2 蹲防走位；两套完全相同的输入喂两遍模拟，逐帧规范哈希必须逐位相同。
	p1Script := func(f int) seat.ScriptedInput {
		switch {
		case f <= 20:
			return seat.ScriptedInput{Direction: 6} // 前进
		case f == 25:
			return seat.ScriptedInput{Direction: 5, Held: input.LP} // 出拳
		default:
			return seat.ScriptedInput{Direction: 5}
		}
	}
	p2Script := func(f int) seat.ScriptedInput {
		if f <= 15 {
			return seat.ScriptedInput{Direction: 2} // 蹲
		}
		return seat.ScriptedInput{Direction: 5}
	}
	trace := func() []uint64 {
		sim := buildSim(p1Script, p2Script, -1.0, 1.0)
		out := make([]uint64, 0, 80)
		for range 80 {
			sim.Tick()
			out = append(out, statehash.HashState(sim))
		}
		return out
	}
	assertSameTrace(t, trace(), trace())
}

func assertSameTrace(t *testing.T, a, b []uint64) {
	t.Helper()
	if len(a) != len(b) {
		t.Fatalf("轨迹长度不同：%d vs %d", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("帧 %d 哈希分叉：%016x vs %016x", i, a[i], b[i])
		}
	}
}
