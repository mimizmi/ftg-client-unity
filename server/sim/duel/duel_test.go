package duel

import (
	"os"
	"path/filepath"
	"testing"

	"google.golang.org/protobuf/proto"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/combat"
	"ftgserver/sim/content"
)

// 跨语言对拍：用真实 Frank 数据 + Replay 输入夹具驱动 Go 权威模拟，逐帧 FrameHash。
// 自验（本地）：同一 Replay 喂两遍必须逐位一致。跨语言（需 Unity 导出的夹具）：
// Go 的 HashLog 必须与 C# 导出的 HashLog 逐帧逐位相同——首个分歧帧即移植 bug 落点。

// repoPath: duel 包目录 = server/sim/duel，仓库根在上三级。
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

// buildReplay 从两条脚本 (帧号 → 方向,按住) 造 Replay。pressed 由 held 边沿推导（点按用）。
func buildReplay(n int, p1, p2 func(f int) (dir, held uint32)) *ftgv1.Replay {
	rep := &ftgv1.Replay{
		Setup: &ftgv1.MatchSetup{
			P1CharacterId: "Frank", P2CharacterId: "Frank",
			Config: &ftgv1.BattleConfig{
				RoundFrames: 99 * 60, IntroFrames: 0, RoundOverFrames: 120,
				RoundsToWin: 2, MaxHealth: 1000,
			},
			ProtocolVersion: 1,
		},
	}
	var prev1, prev2 uint32
	for f := 1; f <= n; f++ {
		d1, h1 := p1(f)
		d2, h2 := p2(f)
		rep.Frames = append(rep.Frames, &ftgv1.FrameInputs{
			Frame: uint32(f),
			P1:    &ftgv1.Input{Direction: d1, Held: h1, Pressed: h1 &^ prev1},
			P2:    &ftgv1.Input{Direction: d2, Held: h2, Pressed: h2 &^ prev2},
		})
		prev1, prev2 = h1, h2
	}
	return rep
}

// TestReplayDeterminism_RealFrank 自验：真实 Frank + 一段有动作的 Replay，两遍逐帧哈希必须逐位一致。
func TestReplayDeterminism_RealFrank(t *testing.T) {
	def := loadFrank(t)

	// P1 走近 → 出拳（LP=1）；P2 中立 + 中途蹲。走位/推挡/朝向翻面都会进哈希。
	p1 := func(f int) (uint32, uint32) {
		switch {
		case f <= 40:
			return 6, 0 // 前进
		case f == 55:
			return 5, 1 // 按 LP
		default:
			return 5, 0
		}
	}
	p2 := func(f int) (uint32, uint32) {
		if f >= 20 && f <= 30 {
			return 2, 0 // 蹲
		}
		return 5, 0
	}
	rep := buildReplay(100, p1, p2)

	a := hashValues(RunReplay(rep, def, def))
	b := hashValues(RunReplay(rep, def, def))

	if len(a) != 100 {
		t.Fatalf("哈希帧数=%d 期望 100", len(a))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("帧 %d 哈希分叉：%016x vs %016x", i+1, a[i], b[i])
		}
	}
	// 反平凡：状态确实演化了（不是"什么都没发生"的假绿）
	if distinct(a) < 2 {
		t.Error("哈希轨迹恒定——模拟未演化，测试无意义")
	}
}

// TestCrossLanguage_HashLogMatchesCSharp 跨语言对拍。夹具未导出则 Skip（与 definition 夹具同套路）。
// 导出步骤：Unity 跑 FG 导出菜单 → 提交 server/testdata/duel_replay.pb + duel_hashlog.pb。
func TestCrossLanguage_HashLogMatchesCSharp(t *testing.T) {
	repBytes, err1 := os.ReadFile(repoPath("server/testdata/duel_replay.pb"))
	logBytes, err2 := os.ReadFile(repoPath("server/testdata/duel_hashlog.pb"))
	if err1 != nil || err2 != nil {
		t.Skip("对拍夹具未导出：需先在 Unity 跑导出菜单并提交 duel_replay.pb + duel_hashlog.pb")
	}

	var rep ftgv1.Replay
	if err := proto.Unmarshal(repBytes, &rep); err != nil {
		t.Fatalf("解析 duel_replay.pb 失败：%v", err)
	}
	var csLog ftgv1.HashLog
	if err := proto.Unmarshal(logBytes, &csLog); err != nil {
		t.Fatalf("解析 duel_hashlog.pb 失败：%v", err)
	}

	def := loadFrank(t) // 镜像内战：双方都是 Frank
	goHashes := RunReplay(&rep, def, def)
	cs := csLog.GetHashes()

	if len(goHashes) != len(cs) {
		t.Fatalf("帧数不一致：Go=%d C#=%d", len(goHashes), len(cs))
	}
	for i := range goHashes {
		if goHashes[i].GetHash() != cs[i].GetHash() {
			t.Fatalf("帧 %d 哈希分歧：Go=%016x C#=%016x（首个分叉帧 = 移植 bug 落点）",
				cs[i].GetFrame(), goHashes[i].GetHash(), cs[i].GetHash())
		}
	}
	t.Logf("对拍通过：%d 帧逐帧逐位一致", len(cs))
}

func hashValues(fh []*ftgv1.FrameHash) []uint64 {
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
