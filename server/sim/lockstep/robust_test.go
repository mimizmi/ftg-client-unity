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

func condName(c NetConditions) string {
	return "lat" + itoa(c.Latency) + "_jit" + itoa(c.Jitter) + "_loss" + itoa(int(c.LossRate*100))
}
