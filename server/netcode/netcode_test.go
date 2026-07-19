package netcode

import (
	"net"
	"path/filepath"
	"testing"
	"time"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/combat"
	"ftgserver/sim/content"
	"ftgserver/sim/duel"
	"ftgserver/sim/input"
	"ftgserver/sim/lockstep"
)

// N6 的收官断言：把 N5 的回滚 peer 挂到【真 UDP socket】上（loopback），两端跑同一局，
// 各自的 confirmed 轨迹必须逐帧逐位一致、且等于单机 duel.RunReplay 参照——
// 证明"确定性回滚"不依赖进程内 Pipe 的理想时序，在真网络栈（序列化/socket/异步收发）下同样成立。
// peer 逻辑一行未改：只是把 Pipe Transport 换成了 ClientTransport。

// netcode 包位于 server/netcode，仓库根在上两级。
func repoPath(rel string) string { return filepath.Join("..", "..", rel) }

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

func p1Script(w int) (uint8, input.ButtonMask) {
	switch {
	case w <= 30:
		return 6, 0
	case w%6 == 0:
		return 5, input.LP
	default:
		return 5, 0
	}
}

func p2Script(w int) (uint8, input.ButtonMask) {
	switch {
	case w >= 15 && w <= 25:
		return 2, 0
	case w%7 == 0:
		return 5, input.LP
	default:
		return 5, 0
	}
}

func scriptForSeat(seat int) lockstep.Script {
	if seat == 1 {
		return p1Script
	}
	return p2Script
}

// referenceTrace：单机把两条脚本（D=0，帧 F 用第 F 次采样）排进 Replay，duel.RunReplay 出参照。
func referenceTrace(t *testing.T, def *combat.FighterDefinition, n int) []uint64 {
	t.Helper()
	rep := &ftgv1.Replay{Setup: matchSetup()}
	var prev1, prev2 input.ButtonMask
	for f := 1; f <= n; f++ {
		d1, h1 := p1Script(f)
		d2, h2 := p2Script(f)
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

func matchSetup() *ftgv1.MatchSetup {
	return &ftgv1.MatchSetup{
		P1CharacterId: "Frank", P2CharacterId: "Frank",
		ProtocolVersion: 1,
		Config: &ftgv1.BattleConfig{
			RoundFrames: 99 * 60, IntroFrames: 0, RoundOverFrames: 120,
			RoundsToWin: 2, MaxHealth: 1000,
		},
	}
}

func TestRollbackOverUDP_ConfirmedTraceMatchesReference(t *testing.T) {
	def := loadFrank(t)
	const n = 120

	// —— 起真 UDP 中继服务器（loopback，随机端口）——
	conn, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 0})
	if err != nil {
		t.Fatalf("监听 UDP 失败：%v", err)
	}
	srv := NewRelayServer(conn, matchSetup(), nil)
	go srv.Serve()
	defer srv.Close()
	addr := srv.Addr().String()

	// —— 两客户端 Dial 真 socket，并发握手直到双方就绪 ——
	join := func() *ftgv1.JoinRequest {
		return &ftgv1.JoinRequest{MatchId: "m1", CharacterId: "Frank", ProtocolVersion: 1}
	}
	ctA, err := Dial(addr, join(), 32)
	if err != nil {
		t.Fatalf("客户端 A Dial 失败：%v", err)
	}
	defer ctA.Close()
	ctB, err := Dial(addr, join(), 32)
	if err != nil {
		t.Fatalf("客户端 B Dial 失败：%v", err)
	}
	defer ctB.Close()

	errc := make(chan error, 2)
	go func() { errc <- ctA.WaitReady(3 * time.Second) }()
	go func() { errc <- ctB.WaitReady(3 * time.Second) }()
	for range 2 {
		if e := <-errc; e != nil {
			t.Fatalf("握手失败：%v", e)
		}
	}

	seatA, seatB := ctA.Seat(), ctB.Seat()
	if seatA == seatB || seatA*seatB != 2 {
		t.Fatalf("座位分配异常：A=%d B=%d（应为 {1,2}）", seatA, seatB)
	}

	// —— 每客户端建一个回滚 peer，Transport 注入各自的 ClientTransport ——
	peerA := lockstep.NewRollbackPeer(lockstep.PeerConfig{
		P1Def: def, P2Def: def, Transport: ctA,
		Script: scriptForSeat(seatA), LocalIsP1: seatA == 1,
	})
	peerB := lockstep.NewRollbackPeer(lockstep.PeerConfig{
		P1Def: def, P2Def: def, Transport: ctB,
		Script: scriptForSeat(seatB), LocalIsP1: seatB == 1,
	})

	// —— 驱动到两端确认帧都 ≥ n（真 UDP 异步到达，用小睡眠让往返完成）——
	const steps = 3000
	reached := false
	for range steps {
		peerA.Advance()
		peerB.Advance()
		if peerA.ConfirmedFrame() >= n && peerB.ConfirmedFrame() >= n {
			reached = true
			break
		}
		time.Sleep(time.Millisecond)
	}
	if !reached {
		t.Fatalf("真网络下未在 %d 步内确认到 %d 帧（A=%d B=%d）",
			steps, n, peerA.ConfirmedFrame(), peerB.ConfirmedFrame())
	}

	// —— 断言：两端 confirmed 轨迹互等且等于单机参照 ——
	ref := referenceTrace(t, def, n)
	assertTrace(t, "A", peerA.ConfirmedTrace(), ref, n)
	assertTrace(t, "B", peerB.ConfirmedTrace(), ref, n)

	t.Logf("真 UDP 回滚：%d 帧确认轨迹逐位一致（A 修正 %d/最大回滚 %d，B 修正 %d/最大回滚 %d）",
		n, peerA.Corrections, peerA.MaxRollback, peerB.Corrections, peerB.MaxRollback)
}

func assertTrace(t *testing.T, who string, got, ref []uint64, n int) {
	t.Helper()
	if len(got) < n {
		t.Fatalf("%s 确认轨迹只有 %d 帧，期望 ≥ %d", who, len(got), n)
	}
	for i := range n {
		if got[i] != ref[i] {
			t.Fatalf("%s 帧 %d 哈希分歧：peer=%016x 参照=%016x（真网络下 desync）",
				who, i+1, got[i], ref[i])
		}
	}
}
