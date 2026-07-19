package combat

import (
	"testing"

	"ftgserver/sim/fixed"
	"ftgserver/sim/motion"
	"ftgserver/sim/seat"
)

// 移动状态机的 Go 镜像。C# 侧移动没有独立单测（经 BattleSimulationTests 间接覆盖），
// 这里补上直接测试：确定输入脚本 → 确定状态/位置轨迹。位置断言全走 Fix.Raw 逐位比较，
// 这正是跨语言 HashLog 对拍要求的位级确定性。

// --- 测试夹具：合成招式数据 + 配置 ---

// mkMove 造一条移动招式：三段帧数 + 每帧位移（面朝右空间）。rm 为 nil = 原地招。
func mkMove(id string, startup, active, recovery int, rm []fixed.Vec2) *MoveData {
	return &MoveData{MoveID: id, Startup: startup, Active: active, Recovery: recovery, RootMotion: rm}
}

// stepX 造一段每帧 +dx（X 方向）的位移轨迹，长度 n。
func stepX(dx float32, n int) []fixed.Vec2 {
	rm := make([]fixed.Vec2, n)
	for i := range rm {
		rm[i] = fixed.Vec2FromFloat(dx, 0)
	}
	return rm
}

// testRig 组装一套可驱动的移动机：配置、招式库、座位、控制器。
type testRig struct {
	moves map[string]*MoveData
	seat  *seat.ScriptedSeat
	mc    *MovementController
	pos   fixed.Vec2
}

func newRig(script func(int) seat.ScriptedInput) *testRig {
	moves := map[string]*MoveData{
		"idle":     mkMove("idle", 0, 1, 0, nil), // 原地循环
		"crouch":   mkMove("crouch", 0, 1, 0, nil),
		"crEnter":  mkMove("crEnter", 0, 6, 0, nil),           // 下蹲过渡 6 帧
		"crExit":   mkMove("crExit", 0, 6, 0, nil),            // 起身过渡 6 帧
		"walkF":    mkMove("walkF", 0, 10, 0, stepX(0.1, 10)), // 每帧 +0.1
		"walkB":    mkMove("walkB", 0, 10, 0, stepX(-0.1, 10)),
		"dash":     mkMove("dash", 2, 6, 4, stepX(0.2, 12)), // 12 帧一次性
		"backdash": mkMove("backdash", 2, 6, 4, stepX(-0.2, 12)),
		"jumpN":    mkMove("jumpN", 3, 6, 3, stepX(0, 12)), // 中立跳（X 不动）
		"jumpF":    mkMove("jumpF", 3, 6, 3, stepX(0.05, 12)),
		"jumpB":    mkMove("jumpB", 3, 6, 3, stepX(-0.05, 12)),
	}
	cfg := &MovementConfig{
		IdleID: "idle", CrouchID: "crouch",
		CrouchEnterID: "crEnter", CrouchExitID: "crExit",
		WalkForwardID: "walkF", WalkBackwardID: "walkB",
		DashID: "dash", BackDashID: "backdash",
		JumpNeutralID: "jumpN", JumpForwardID: "jumpF", JumpBackwardID: "jumpB",
		AirDashCount: 1,
	}
	s := seat.NewScriptedSeat(script)
	s.Detector().Add(motion.DashForward())
	s.Detector().Add(motion.DashBackward())
	mc := NewMovementController(cfg, s, func(id string) *MoveData { return moves[id] })
	return &testRig{moves: moves, seat: s, mc: mc}
}

// step 推进 n 帧：每帧先座位采样（喂 Buffer/Commands），再驱动移动机。actionable 恒真（纯移动测试）。
func (r *testRig) step(facingRight bool, n int) {
	for range n {
		r.seat.ManualTick()
		r.mc.Tick(true, facingRight, &r.pos)
	}
}

// hold 恒定方向脚本。
func hold(dir uint8) func(int) seat.ScriptedInput {
	return func(int) seat.ScriptedInput { return seat.ScriptedInput{Direction: dir} }
}

// --- 测试 ---

func TestIdle_NoInput_StaysZero(t *testing.T) {
	r := newRig(hold(5)) // 中立
	r.step(true, 20)

	if r.mc.State() != MovementIdle {
		t.Errorf("状态=%d 期望 Idle", r.mc.State())
	}
	if !r.pos.Eq(fixed.Vec2Zero) {
		t.Errorf("位置=%+v 期望零（待机无位移）", r.pos)
	}
}

func TestWalkForward_AccumulatesRootMotion(t *testing.T) {
	r := newRig(hold(6)) // 一直按前
	r.step(true, 10)

	if r.mc.State() != MovementWalkForward {
		t.Fatalf("状态=%d 期望 WalkForward", r.mc.State())
	}
	// 位置 = 逐帧定点增量精确求和：10 × FromFloat(0.1).Raw（0.1 在 Q16.16 非精确，
	// 累加 6554×10=65540≠65536——这正是定点确定性：结果 = 增量之和，逐位可预测）。
	want := fixed.FromFloat(0.1).Raw * 10
	if r.pos.X.Raw != want {
		t.Errorf("X.Raw=%d 期望 %d（10×FromFloat(0.1)）", r.pos.X.Raw, want)
	}
	if r.pos.Y.Raw != 0 {
		t.Errorf("Y.Raw=%d 期望 0", r.pos.Y.Raw)
	}
}

func TestWalkForward_FacingLeft_MirroredNegative(t *testing.T) {
	// 面朝左：世界方向 4（左）经 Mirror→6 逻辑前，motionMirrored 令位移 X 取反 → 世界系向左走
	r := newRig(hold(4))
	r.step(false, 10)

	if r.mc.State() != MovementWalkForward {
		t.Fatalf("状态=%d 期望 WalkForward（朝左时世界左=逻辑前）", r.mc.State())
	}
	if r.pos.X.Raw >= 0 {
		t.Errorf("X.Raw=%d 期望负（朝左前进即世界向左）", r.pos.X.Raw)
	}
}

func TestWalk_ReleaseReturnsToIdle(t *testing.T) {
	r := newRig(func(f int) seat.ScriptedInput {
		if f <= 5 {
			return seat.ScriptedInput{Direction: 6} // 先走 5 帧
		}
		return seat.ScriptedInput{Direction: 5} // 松开
	})
	r.step(true, 5)
	if r.mc.State() != MovementWalkForward {
		t.Fatalf("前 5 帧状态=%d 期望 WalkForward", r.mc.State())
	}
	r.step(true, 3)
	if r.mc.State() != MovementIdle {
		t.Errorf("松开后状态=%d 期望 Idle", r.mc.State())
	}
}

func TestJump_LaunchesAirborneAndLands(t *testing.T) {
	// 帧 1 中立（建立 Idle、wasUp=false），帧 2 起按上（边沿）触发跳，之后持续中立到落地
	r := newRig(func(f int) seat.ScriptedInput {
		if f == 2 {
			return seat.ScriptedInput{Direction: 8}
		}
		return seat.ScriptedInput{Direction: 5}
	})
	r.step(true, 1) // 中立
	r.step(true, 1) // 上边沿 → 起跳
	if !r.mc.IsJumping() {
		t.Fatal("上边沿应起跳")
	}
	// 起跳预备段仍在地面，不算 Airborne
	if r.mc.IsAirborne() {
		t.Error("起跳预备段(Startup)不应算 Airborne")
	}
	// 推进过 Startup(3) 进入 Active → Airborne
	r.step(true, 4)
	if !r.mc.IsAirborne() {
		t.Errorf("腾空段应 Airborne（phase=%d）", r.mc.Phase())
	}
	// 跑完剩余帧落地：jumpN 共 12 帧，起跳那帧 motionFrame=1，再约 12 帧到头
	r.step(true, 12)
	if r.mc.State() != MovementIdle {
		t.Errorf("落地后状态=%d 期望 Idle", r.mc.State())
	}
}

func TestJump_HeldUp_NoRetrigger(t *testing.T) {
	// 全程按住上：只应起跳一次，落地后按住不放不得连发
	r := newRig(hold(8))
	r.step(true, 1) // 第 1 帧：isUp 但 wasUp 初值 false → 会起跳
	if !r.mc.IsJumping() {
		t.Fatal("首帧上边沿应起跳")
	}
	r.step(true, 30) // 足够落地并继续按住
	// 落地后仍按住上 → wasUpLastFrame 为真 → grounded 不再起跳，停在 Idle
	if r.mc.IsJumping() {
		t.Error("按住上不放不得在落地后连发跳")
	}
	if r.mc.State() != MovementIdle {
		t.Errorf("状态=%d 期望 Idle（按住上落地后待机）", r.mc.State())
	}
}

func TestDash_ConsumesCommandAndAdvances(t *testing.T) {
	// 66 双击：6(帧1) 5(帧2) 6(帧3) → 帧3 检出 DASH_F
	r := newRig(func(f int) seat.ScriptedInput {
		switch f {
		case 1:
			return seat.ScriptedInput{Direction: 6}
		case 2:
			return seat.ScriptedInput{Direction: 5}
		case 3:
			return seat.ScriptedInput{Direction: 6}
		default:
			return seat.ScriptedInput{Direction: 5}
		}
	})
	r.step(true, 3)
	if r.mc.State() != MovementDash {
		t.Fatalf("状态=%d 期望 Dash（66 应触发冲刺）", r.mc.State())
	}
	beforeX := r.pos.X.Raw
	r.step(true, 1)
	if r.pos.X.Raw <= beforeX {
		t.Errorf("冲刺应推进 X（%d → %d）", beforeX, r.pos.X.Raw)
	}
}

func TestCrouch_ZeroDisplacement(t *testing.T) {
	r := newRig(hold(2)) // 一直按下
	r.step(true, 1)
	if r.mc.State() != MovementCrouchEnter {
		t.Fatalf("状态=%d 期望 CrouchEnter", r.mc.State())
	}
	r.step(true, 20) // 蹲到底进循环
	if r.mc.State() != MovementCrouch {
		t.Errorf("状态=%d 期望 Crouch（过渡后进循环）", r.mc.State())
	}
	if !r.pos.Eq(fixed.Vec2Zero) {
		t.Errorf("位置=%+v 期望零（蹲姿全程无位移）", r.pos)
	}
}

func TestCrouch_MirrorProgressOnRelease(t *testing.T) {
	// 下蹲过渡到第 3 帧改主意松开 → 起身从对称帧接续
	enter := mkMove("crEnter", 0, 6, 0, nil)
	exit := mkMove("crExit", 0, 6, 0, nil)
	r := newRig(func(f int) seat.ScriptedInput {
		if f <= 3 {
			return seat.ScriptedInput{Direction: 2}
		}
		return seat.ScriptedInput{Direction: 5}
	})
	r.step(true, 3)
	if r.mc.State() != MovementCrouchEnter {
		t.Fatalf("状态=%d 期望 CrouchEnter", r.mc.State())
	}
	p := r.mc.MotionFrame()
	r.step(true, 1) // 松开当帧转 CrouchExit
	if r.mc.State() != MovementCrouchExit {
		t.Fatalf("状态=%d 期望 CrouchExit", r.mc.State())
	}
	want := mirrorProgress(enter, p, exit)
	if r.mc.MotionFrame() != want {
		t.Errorf("起身接续帧=%d 期望 %d（对称镜像 p=%d）", r.mc.MotionFrame(), want, p)
	}
}

func TestDeterminism_SameScriptSameTrace(t *testing.T) {
	// 同一脚本跑两遍，逐帧位置必须 Fix.Raw 逐位相同——headless 对拍确定性的本地自证。
	script := func(f int) seat.ScriptedInput {
		switch {
		case f <= 8:
			return seat.ScriptedInput{Direction: 6} // 走
		case f <= 12:
			return seat.ScriptedInput{Direction: 2} // 蹲
		case f == 14:
			return seat.ScriptedInput{Direction: 8} // 跳
		default:
			return seat.ScriptedInput{Direction: 5}
		}
	}
	trace := func() []fixed.Vec2 {
		r := newRig(script)
		out := make([]fixed.Vec2, 0, 40)
		for range 40 {
			r.seat.ManualTick()
			r.mc.Tick(true, true, &r.pos)
			out = append(out, r.pos)
		}
		return out
	}
	a := trace()
	b := trace()
	for i := range a {
		if a[i].X.Raw != b[i].X.Raw || a[i].Y.Raw != b[i].Y.Raw {
			t.Fatalf("帧 %d 轨迹分叉：%+v vs %+v", i, a[i], b[i])
		}
	}
}
