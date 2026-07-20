package lockstep

import "testing"

// DelayController 单元测试：稳态 RTT → 推荐 D≈RTT/2；夹取上下限；抖动下 EWMA 稳住不乱跳。

func converge(c *DelayController, rtt, iters int) int {
	var d int
	for range iters {
		d = c.Observe(rtt)
	}
	return d
}

func TestDelayController_ConvergesToHalfRtt(t *testing.T) {
	cases := []struct{ rtt, wantD int }{
		{0, 0}, {2, 1}, {6, 3}, {8, 4}, {12, 6},
	}
	for _, tc := range cases {
		c := NewDelayController(0, 16)
		if got := converge(c, tc.rtt, 80); got != tc.wantD {
			t.Errorf("稳态 RTT=%d 应推荐 D=%d，得 %d", tc.rtt, tc.wantD, got)
		}
	}
}

func TestDelayController_Clamps(t *testing.T) {
	hi := NewDelayController(0, 4)
	if got := converge(hi, 20, 80); got != 4 {
		t.Errorf("RTT=20 应被上限夹到 D=4，得 %d", got)
	}
	lo := NewDelayController(2, 8)
	if got := converge(lo, 0, 80); got != 2 {
		t.Errorf("RTT=0 应被下限夹到 D=2，得 %d", got)
	}
}

func TestDelayController_StableUnderJitter(t *testing.T) {
	c := NewDelayController(0, 10)
	converge(c, 8, 80) // 稳态 D=4
	// 围绕 RTT=8 的对称抖动（6/10 交替），EWMA 应把推荐 D 稳在 4，不逐样本乱跳。
	for i := range 60 {
		rtt := 6
		if i%2 == 0 {
			rtt = 10
		}
		if got := c.Observe(rtt); got != 4 {
			t.Fatalf("抖动第 %d 样本推荐 D=%d，期望稳定在 4", i, got)
		}
	}
}
