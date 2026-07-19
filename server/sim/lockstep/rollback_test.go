package lockstep

import "testing"

// 回滚（N5-②）的核心断言：无论预测错多少、回滚多少帧，两端【确认轨迹】逐位一致、
// 且等于单机 duel.RunReplay 参照——回滚只改"何时看到正确结果"，不改"最终正确结果"。
// 再加一条"回滚真的在工作"的存在性证明：Corrections>0（确有误预测被真输入纠正）。

// TestRollback_ConfirmedTraceMatchesReference：D=0（本地即时）、L=3 有延迟。
// 两端 confirmed 轨迹互等且等于参照；且确有预测被回滚修正、重模拟窗口 ≥ 延迟。
func TestRollback_ConfirmedTraceMatchesReference(t *testing.T) {
	def := loadFrank(t)
	const n, d, l = 120, 0, 3

	m := NewRollbackMatch(MatchConfig{
		P1Def: def, P2Def: def,
		P1Script: p1Script, P2Script: p2Script,
		InputDelay: d, Latency: l,
	})
	if err := m.RunFrames(n); err != nil {
		t.Fatal(err)
	}

	ref := referenceTrace(t, def, n, d)
	assertTrace(t, "A.confirmed", m.A.ConfirmedTrace(), ref, n)
	assertTrace(t, "B.confirmed", m.B.ConfirmedTrace(), ref, n)

	// 反平凡：状态确实演化。
	if distinct(ref[:n]) < 2 {
		t.Error("参照轨迹恒定——模拟未演化，测试无意义")
	}
	// 回滚真的在工作：远端输入随脚本变化，L>0 下必有误预测被纠正、且重模拟窗口 ≥ 1。
	if m.A.Corrections == 0 && m.B.Corrections == 0 {
		t.Error("Corrections=0——没有任何预测被修正，回滚未被真正触发")
	}
	if m.A.MaxRollback < l || m.B.MaxRollback < l {
		t.Errorf("重模拟窗口过小：A=%d B=%d，期望 ≥ L=%d（延迟应被预测藏住）",
			m.A.MaxRollback, m.B.MaxRollback, l)
	}
	t.Logf("回滚生效：A{修正 %d, 最大回滚 %d 帧} B{修正 %d, 最大回滚 %d 帧}；120 帧确认轨迹与单机参照逐位一致",
		m.A.Corrections, m.A.MaxRollback, m.B.Corrections, m.B.MaxRollback)
}

// TestRollback_VariousLatencies：扫多档延迟。① 确认轨迹恒等于参照（延迟不改最终结果）；
// ② 回滚窗口随延迟【单调加深】——延迟越大预测得越远、回滚重模拟得越深，这正是回滚的代价曲线。
func TestRollback_VariousLatencies(t *testing.T) {
	def := loadFrank(t)
	const n, d = 90, 0
	latencies := []int{0, 2, 5}
	ref := referenceTrace(t, def, n, d)

	var prevMaxRB int
	for i, l := range latencies {
		t.Run("L"+itoa(l), func(t *testing.T) {
			m := NewRollbackMatch(MatchConfig{
				P1Def: def, P2Def: def,
				P1Script: p1Script, P2Script: p2Script,
				InputDelay: d, Latency: l,
			})
			if err := m.RunFrames(n); err != nil {
				t.Fatal(err)
			}
			assertTrace(t, "A.confirmed", m.A.ConfirmedTrace(), ref, n)
			assertTrace(t, "B.confirmed", m.B.ConfirmedTrace(), ref, n)

			// 回滚窗口随延迟单调加深（本夹具有效单程延迟 ≈ L+1，故 MaxRollback ≈ L+1）。
			maxRB := max(m.A.MaxRollback, m.B.MaxRollback)
			if i > 0 && maxRB <= prevMaxRB {
				t.Errorf("L=%d 的最大回滚窗口 %d 未比上一档 %d 更深——延迟未转化为更深回滚",
					l, maxRB, prevMaxRB)
			}
			prevMaxRB = maxRB
			t.Logf("L=%d：最大回滚 %d 帧，修正 A=%d B=%d", l, maxRB, m.A.Corrections, m.B.Corrections)
		})
	}
}
