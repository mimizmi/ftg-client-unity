package lockstep

import "testing"

// 网络健壮性断言：恶劣链路（丢包/抖动/乱序）下，回滚 + 冗余窗口的 confirmed 轨迹仍逐位等于
// 单机参照——丢包只推迟"何时确认"，不改"确认成什么"。外加对照：无冗余（窗口=1）时单个丢包即永久卡死，
// 凸显冗余窗口的价值。全部确定性（种子固定）、可复现。

// TestRobust_SurvivesLossJitterReorder：多档恶劣链路，W=32，confirmed 轨迹恒等于参照。
func TestRobust_SurvivesLossJitterReorder(t *testing.T) {
	def := loadFrank(t)
	const n = 100
	ref := referenceTrace(t, def, n, 0)

	cases := []NetConditions{
		{Latency: 2, Jitter: 2, LossRate: 0.10, Seed: 1},
		{Latency: 3, Jitter: 4, LossRate: 0.30, Seed: 7}, // 高丢包 + 强抖动（乱序）
		{Latency: 1, Jitter: 0, LossRate: 0.20, Seed: 42},
	}
	for _, cond := range cases {
		t.Run(condName(cond), func(t *testing.T) {
			m := NewRobustMatch(MatchConfig{
				P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script,
			}, cond, 32)
			if err := m.RunFrames(n, n*5+500); err != nil {
				t.Fatal(err)
			}
			assertTrace(t, "A", m.A.ConfirmedTrace(), ref, n)
			assertTrace(t, "B", m.B.ConfirmedTrace(), ref, n)

			// 确有丢包发生（否则这条测试没测到韧性）。
			aSent, aDrop, bSent, bDrop := m.LossStats()
			if aDrop == 0 && bDrop == 0 {
				t.Errorf("两向都零丢包，未真正验证抗丢包（aSent=%d bSent=%d）", aSent, bSent)
			}
			t.Logf("%s：确认 100 帧逐位一致；丢包 aToB=%d/%d bToA=%d/%d，最大回滚 A=%d B=%d",
				condName(cond), aDrop, aSent, bDrop, bSent, m.A.MaxRollback, m.B.MaxRollback)
		})
	}
}

// TestRobust_NoRedundancyStalls_RedundancyRecovers：同一恶劣链路下，无冗余(W=1)永久卡死，
// 冗余(W=32)照常收敛且正确——冗余窗口是抗丢包的关键，一测见分晓。
func TestRobust_NoRedundancyStalls_RedundancyRecovers(t *testing.T) {
	def := loadFrank(t)
	const n = 120
	cond := NetConditions{Latency: 1, Jitter: 0, LossRate: 0.30, Seed: 5}

	// 无冗余：单个数据报丢失 = 该帧永久缺失 → 确认卡死。
	noRedund := NewRobustMatch(MatchConfig{
		P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script,
	}, cond, 1)
	if err := noRedund.RunFrames(n, 800); err == nil {
		t.Fatalf("无冗余(W=1)在 30%% 丢包下竟未卡死——冗余对照失效（确认 A=%d B=%d）",
			noRedund.A.ConfirmedFrame(), noRedund.B.ConfirmedFrame())
	} else {
		t.Logf("对照：无冗余(W=1)如预期卡死 → %v", err)
	}

	// 冗余：同样 30% 丢包，照常收敛且逐位正确。
	redund := NewRobustMatch(MatchConfig{
		P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script,
	}, cond, 32)
	if err := redund.RunFrames(n, n*5+500); err != nil {
		t.Fatalf("冗余(W=32)在 30%% 丢包下应能收敛，却失败：%v", err)
	}
	ref := referenceTrace(t, def, n, 0)
	assertTrace(t, "A", redund.A.ConfirmedTrace(), ref, n)
	assertTrace(t, "B", redund.B.ConfirmedTrace(), ref, n)
	_, aDrop, _, bDrop := redund.LossStats()
	t.Logf("冗余(W=32)在丢包 aToB=%d bToA=%d 下确认 %d 帧逐位一致", aDrop, bDrop, n)
}

// TestRobust_AckTrimsBandwidth：发送端用对端 ack 裁剪重发窗口——不再无脑重发满 W 帧，
// 只重发对端尚未确认的帧。带宽（累计发出帧数）应远低于"无裁剪每次发 min(帧数,W)"的基线，
// 同时 confirmed 轨迹仍逐位正确（省带宽不损正确性）。
func TestRobust_AckTrimsBandwidth(t *testing.T) {
	def := loadFrank(t)
	const n, w = 120, 32
	cond := NetConditions{Latency: 2, Jitter: 0, LossRate: 0, Seed: 1}

	m := NewRobustMatch(MatchConfig{
		P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script,
	}, cond, w)
	if err := m.RunFrames(n, n*3+200); err != nil {
		t.Fatal(err)
	}

	// 正确性不受裁剪影响。
	ref := referenceTrace(t, def, n, 0)
	assertTrace(t, "A", m.A.ConfirmedTrace(), ref, n)
	assertTrace(t, "B", m.B.ConfirmedTrace(), ref, n)

	// 无裁剪基线：每帧发 min(已发帧数, W) 个。裁剪后应低于基线的一半。
	baseline := 0
	for f := 1; f <= n; f++ {
		baseline += min(f, w)
	}
	aFrames, bFrames := m.FramesSent()
	if aFrames >= baseline/2 || bFrames >= baseline/2 {
		t.Errorf("ack 裁剪未显著省带宽：发出帧数 aToB=%d bToA=%d，基线=%d（应 < 基线/2=%d）",
			aFrames, bFrames, baseline, baseline/2)
	}
	// 保底：每步至少发最新一帧，故不应少于步数级别。
	if aFrames < n || bFrames < n {
		t.Errorf("发出帧数异常偏低 aToB=%d bToA=%d（应 ≥ %d）", aFrames, bFrames, n)
	}
	t.Logf("ack 裁剪省带宽：发出帧数 aToB=%d bToA=%d（无裁剪基线=%d，省 %.0f%%）；确认 %d 帧逐位一致",
		aFrames, bFrames, baseline, 100*(1-float64(aFrames)/float64(baseline)), n)
}

// TestRobust_ConnectionStats_RttTracksLatency：连接质量的 RTT（帧）估计应随链路延迟单调增大、
// 且约等于 2L（往返）。这是不依赖墙钟的延迟观测，给 C# 客户端做延迟显示/自适应铺路。
func TestRobust_ConnectionStats_RttTracksLatency(t *testing.T) {
	def := loadFrank(t)
	const n = 100
	statsFor := func(l int) ConnStats {
		m := NewRobustMatch(MatchConfig{P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script},
			NetConditions{Latency: l, Seed: 1}, 32)
		if err := m.RunFrames(n, n*3+200); err != nil {
			t.Fatal(err)
		}
		return m.StatsA()
	}
	near := statsFor(2)
	far := statsFor(6)
	t.Logf("RTT(帧)：L=2 → %d，L=6 → %d；新鲜度 L=2 → %d，L=6 → %d",
		near.RttFrames, far.RttFrames, near.StaleSteps, far.StaleSteps)

	if far.RttFrames <= near.RttFrames {
		t.Errorf("RTT 未随延迟增大：L=2 得 %d，L=6 得 %d", near.RttFrames, far.RttFrames)
	}
	if near.RttFrames < 2 || near.RttFrames > 6 { // ≈2L=4，±L 容差
		t.Errorf("L=2 的 RTT=%d 偏离期望 ~4（应在 [2,6]）", near.RttFrames)
	}
	if far.RttFrames < 6 || far.RttFrames > 18 { // ≈2L=12，±L 容差
		t.Errorf("L=6 的 RTT=%d 偏离期望 ~12（应在 [6,18]）", far.RttFrames)
	}
	if near.StaleSteps > 3 {
		t.Errorf("好链路新鲜度应很小，得 %d", near.StaleSteps)
	}
}

// TestRobust_StaleGrowsWhenPeerSilent：对家静默后，本端"远端新鲜度"（连续无新远端帧的步数）
// 应随之增长——这是掉线检测的基础信号。
func TestRobust_StaleGrowsWhenPeerSilent(t *testing.T) {
	def := loadFrank(t)
	m := NewRobustMatch(MatchConfig{P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script},
		NetConditions{Latency: 1, Seed: 1}, 32)
	if err := m.RunFrames(60, 400); err != nil {
		t.Fatal(err)
	}
	if s := m.StatsA(); s.StaleSteps > 3 {
		t.Fatalf("热身后好链路新鲜度应很小，得 %d", s.StaleSteps)
	}

	// 对家掉线：只推进 A 与链路时钟，B 不再发帧。
	const silent = 20
	for range silent {
		m.A.Advance()
		m.aToB.Step()
		m.bToA.Step()
	}
	s := m.StatsA()
	t.Logf("对家静默 %d 步后，A 远端新鲜度 = %d 步", silent, s.StaleSteps)
	if s.StaleSteps < silent-3 { // 扣掉在途 L 帧的余量
		t.Errorf("对家静默后新鲜度应逼近 %d，仅得 %d", silent, s.StaleSteps)
	}
}

// TestDelayController_TracksMeasuredRtt：把【真实跑出来的】连接质量 RttFrames 喂给 DelayController，
// 推荐的输入延迟 D 应随实测 RTT 增大、且 ≈ round(RTT/2)。这条把 ConnStats 与 DelayController 串起来——
// 实机里客户端就是这样用测得的 RTT 去挑输入延迟的。
func TestDelayController_TracksMeasuredRtt(t *testing.T) {
	def := loadFrank(t)
	const n = 100
	recommend := func(l int) (rtt, d int) {
		m := NewRobustMatch(MatchConfig{P1Def: def, P2Def: def, P1Script: p1Script, P2Script: p2Script},
			NetConditions{Latency: l, Seed: 1}, 32)
		if err := m.RunFrames(n, n*3+200); err != nil {
			t.Fatal(err)
		}
		rtt = m.StatsA().RttFrames
		c := NewDelayController(0, 16)
		for range 40 { // 喂若干次同一观测让 EWMA 收敛（实机每帧喂）
			d = c.Observe(rtt)
		}
		return rtt, d
	}
	rttNear, dNear := recommend(2)
	rttFar, dFar := recommend(6)
	t.Logf("实测 RTT → 推荐 D：L=2 (RTT %d → D %d)，L=6 (RTT %d → D %d)", rttNear, dNear, rttFar, dFar)

	if dFar <= dNear {
		t.Errorf("推荐 D 未随实测 RTT 增大：L=2→%d，L=6→%d", dNear, dFar)
	}
	if dNear < rttNear/2 || dNear > rttNear/2+1 { // ≈round(RTT/2)
		t.Errorf("L=2：推荐 D=%d 偏离 round(RTT/2)（RTT=%d）", dNear, rttNear)
	}
	if dFar < rttFar/2 || dFar > rttFar/2+1 {
		t.Errorf("L=6：推荐 D=%d 偏离 round(RTT/2)（RTT=%d）", dFar, rttFar)
	}
}

func condName(c NetConditions) string {
	return "lat" + itoa(c.Latency) + "_jit" + itoa(c.Jitter) + "_loss" + itoa(int(c.LossRate*100))
}
