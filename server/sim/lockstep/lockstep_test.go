package lockstep

import (
	"path/filepath"
	"testing"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/combat"
	"ftgserver/sim/content"
	"ftgserver/sim/duel"
	"ftgserver/sim/input"
)

// 帧同步（N5 第一阶段）的核心断言有两条：
//   ① 两端逐帧 StateHash 逐位一致——"两台机器打同一局"看到完全相同的确定性演化。
//   ② 两端轨迹与【单机】duel.RunReplay 把同一批输入（含输入延迟排布）跑出来的参照逐位一致
//      ——证明帧同步只是"把确定的输入流分两端喂"，没有引入任何非确定性。
// 这把 N4 的跨语言确定性证明延伸成了网络语义的正确性证明。

func repoPath(rel string) string { return filepath.Join("..", "..", "..", rel) }

func loadFrank(t *testing.T) *combat.FighterDefinition {
	t.Helper()
	def, err := content.LoadCharacter(
		repoPath("server/testdata/frank_definition.pb"),
		repoPath("Assets/BoxData/Frank_boxes.json"),
		repoPath("Assets/BoxData/Frank_rootmotion.json"))
	if err != nil {
		t.Fatalf("装载 Frank 失败：%v", err)
	}
	return def
}

// 有动作的双方脚本：P1 前进逼近后连点 LP；P2 中途蹲、也连点 LP。
// 走位/推挡/朝向翻面/出招/受击/顿帧都会进哈希——密集采样判定链。
func p1Script(w int) (uint8, input.ButtonMask) {
	switch {
	case w <= 30:
		return 6, 0 // 前进
	case w%6 == 0:
		return 5, input.LP // 连点 LP
	default:
		return 5, 0
	}
}

func p2Script(w int) (uint8, input.ButtonMask) {
	switch {
	case w >= 15 && w <= 25:
		return 2, 0 // 蹲
	case w%7 == 0:
		return 5, input.LP
	default:
		return 5, 0
	}
}

// referenceTrace 用单机 duel.RunReplay 造参照：把两条脚本按输入延迟 D 排布进逐帧 Replay
// （sim 帧 F 的输入 = 墙钟帧 F-D 的采样，F≤D 为中立），pressed 边沿在采样流上推导。
// 这与 Peer.Advance 的排布/推导逐位对应，但由完全独立的一段代码算出——是真·独立参照。
func referenceTrace(t *testing.T, def *combat.FighterDefinition, n, d int) []uint64 {
	t.Helper()
	rep := &ftgv1.Replay{
		Setup: &ftgv1.MatchSetup{
			P1CharacterId: "Frank", P2CharacterId: "Frank",
			ProtocolVersion: 1,
			Config: &ftgv1.BattleConfig{
				RoundFrames: 99 * 60, IntroFrames: 0, RoundOverFrames: 120,
				RoundsToWin: 2, MaxHealth: 1000,
			},
		},
	}
	var prev1, prev2 input.ButtonMask
	for f := 1; f <= n; f++ {
		d1, h1 := uint8(5), input.ButtonMask(0)
		d2, h2 := uint8(5), input.ButtonMask(0)
		if f > d {
			d1, h1 = p1Script(f - d)
			d2, h2 = p2Script(f - d)
		}
		rep.Frames = append(rep.Frames, &ftgv1.FrameInputs{
			Frame: uint32(f),
			P1:    &ftgv1.Input{Direction: uint32(d1), Held: uint32(h1), Pressed: uint32(h1 &^ prev1)},
			P2:    &ftgv1.Input{Direction: uint32(d2), Held: uint32(h2), Pressed: uint32(h2 &^ prev2)},
		})
		prev1, prev2 = h1, h2
	}

	fh := duel.RunReplay(rep, def, def)
	out := make([]uint64, len(fh))
	for i, h := range fh {
		out[i] = h.GetHash()
	}
	return out
}

func distinct(vs []uint64) int {
	seen := make(map[uint64]struct{}, len(vs))
	for _, v := range vs {
		seen[v] = struct{}{}
	}
	return len(seen)
}

// TestLockstep_TwoPeersMatchAndEqualReference：D≥L 的常规局，两端逐帧一致且与单机参照一致。
func TestLockstep_TwoPeersMatchAndEqualReference(t *testing.T) {
	def := loadFrank(t)
	const n, d, l = 120, 3, 2

	m := NewMatch(MatchConfig{
		P1Def: def, P2Def: def,
		P1Script: p1Script, P2Script: p2Script,
		InputDelay: d, Latency: l,
	})
	if err := m.RunFrames(n); err != nil {
		t.Fatal(err)
	}

	ref := referenceTrace(t, def, n, d)
	assertTrace(t, "A", m.A.Trace(), ref, n)
	assertTrace(t, "B", m.B.Trace(), ref, n)

	// 反平凡：状态确实演化了（不是"什么都没发生"的假绿）。
	if distinct(ref[:n]) < 2 {
		t.Error("参照轨迹恒定——模拟未演化，测试无意义")
	}
	// 抽查语义：P1 前进 30 帧后应处于 P2 左侧（推挡/走位真的发生了）。
	if !m.A.Sim().P1.Position.X.Lt(m.A.Sim().P2.Position.X) {
		t.Error("P1 未处于 P2 左侧——走位/朝向异常")
	}
}

// TestLockstep_ToleratesVariousDelays：延迟只影响推进节奏、不影响正确性。
// 遍历若干 (D,L)——含 D<L（本端落后但仍收敛）——两端与参照必须始终逐位一致。
func TestLockstep_ToleratesVariousDelays(t *testing.T) {
	def := loadFrank(t)
	const n = 90
	cases := []struct{ d, l int }{
		{0, 0}, // 无延迟无网络
		{2, 2}, // D=L 临界
		{4, 2}, // D>L 富余
		{1, 3}, // D<L 落后但收敛（可靠传输不丢包）
	}
	for _, c := range cases {
		t.Run(subName(c.d, c.l), func(t *testing.T) {
			m := NewMatch(MatchConfig{
				P1Def: def, P2Def: def,
				P1Script: p1Script, P2Script: p2Script,
				InputDelay: c.d, Latency: c.l,
			})
			if err := m.RunFrames(n); err != nil {
				t.Fatal(err)
			}
			ref := referenceTrace(t, def, n, c.d)
			assertTrace(t, "A", m.A.Trace(), ref, n)
			assertTrace(t, "B", m.B.Trace(), ref, n)
		})
	}
}

func assertTrace(t *testing.T, who string, got, ref []uint64, n int) {
	t.Helper()
	if len(got) < n {
		t.Fatalf("%s 只推进了 %d 帧，期望 ≥ %d", who, len(got), n)
	}
	for i := range n {
		if got[i] != ref[i] {
			t.Fatalf("%s 帧 %d 哈希分歧：peer=%016x 参照=%016x（首个分叉帧 = desync 落点）",
				who, i+1, got[i], ref[i])
		}
	}
}

func subName(d, l int) string { return "D" + itoa(d) + "_L" + itoa(l) }

func itoa(v int) string {
	if v == 0 {
		return "0"
	}
	var b [4]byte
	i := len(b)
	for v > 0 {
		i--
		b[i] = byte('0' + v%10)
		v /= 10
	}
	return string(b[i:])
}
