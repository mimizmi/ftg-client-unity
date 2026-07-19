package motion

import (
	"testing"

	"ftgserver/sim/input"
)

// 搓招识别的 Go 镜像，对齐 C# MotionDetectorTests：确定输入序列 → 确定识别结果。
// 这些是输入层的帧级契约，回滚重放输入时两端必须得到完全相同的识别。

type harness struct {
	buf   *input.Buffer
	det   *Detector
	frame int
}

func newHarness() *harness {
	return &harness{buf: input.NewBuffer(120), det: &Detector{}}
}

func (h *harness) push(dir uint8, pressed input.ButtonMask) {
	h.frame++
	h.buf.Push(input.Frame{
		Frame: h.frame, Direction: dir, Held: pressed, Pressed: pressed,
	})
}

func (h *harness) detect(facingRight bool) []*Pattern {
	return h.det.DetectAll(h.buf, facingRight)
}

func TestQcf_CleanSequence(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	h.push(5, 0)
	h.push(5, 0)
	h.push(2, 0)
	h.push(3, 0)
	h.push(6, input.LP)

	r := h.detect(true)
	if len(r) != 1 || r[0].ID != "QCF_P" {
		t.Fatalf("识别结果=%v 期望 [QCF_P]", ids(r))
	}
}

func TestQcf_WithSlack_InsideWindow(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	h.push(2, 0)
	h.push(2, 0)
	h.push(2, 0)
	h.push(3, 0)
	h.push(3, 0)
	h.push(3, 0)
	h.push(6, 0)
	h.push(6, input.LP)

	if len(h.detect(true)) != 1 {
		t.Fatal("窗口内的磨蹭应识别")
	}
}

func TestQcf_ExceedsTotalWindow_Rejected(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	h.push(2, 0)
	for range 22 {
		h.push(5, 0)
	}
	h.push(3, 0)
	h.push(6, input.LP)

	if len(h.detect(true)) != 0 {
		t.Fatal("超出总窗口应拒绝")
	}
}

func TestQcf_ButtonWithoutMotion_Rejected(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	h.push(5, 0)
	h.push(5, 0)
	h.push(5, input.LP)

	if len(h.detect(true)) != 0 {
		t.Fatal("无方向序列的裸按键应拒绝")
	}
}

func TestQcf_FacingLeft_Mirrored(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	// 面朝左：世界方向 2,1,4 = 逻辑 2,3,6
	h.push(5, 0)
	h.push(2, 0)
	h.push(1, 0)
	h.push(4, input.LP)

	if len(h.detect(false)) != 1 {
		t.Error("面朝左应按镜像识别")
	}
	if len(h.detect(true)) != 0 {
		t.Error("面朝右时同一世界输入不该识别")
	}
}

func TestDp_CleanSequence(t *testing.T) {
	h := newHarness()
	h.det.Add(Dp("DP_P", input.LP))
	h.push(5, 0)
	h.push(6, 0)
	h.push(2, 0)
	h.push(3, input.LP)

	r := h.detect(true)
	if len(r) != 1 || r[0].ID != "DP_P" {
		t.Fatalf("识别=%v 期望 [DP_P]", ids(r))
	}
}

func TestDp_WinsOverQcf(t *testing.T) {
	h := newHarness()
	h.det.Add(Qcf("QCF_P", input.LP))
	h.det.Add(Dp("DP_P", input.LP))
	// 6-2-3-6+P 同时满足 236 与 623，歧义时升龙优先
	h.push(5, 0)
	h.push(6, 0)
	h.push(2, 0)
	h.push(3, 0)
	h.push(6, input.LP)

	r := h.detect(true)
	if len(r) != 1 || r[0].ID != "DP_P" {
		t.Fatalf("识别=%v 期望 [DP_P]（升龙优先）", ids(r))
	}
}

func TestDashForward_DoubleTap(t *testing.T) {
	h := newHarness()
	h.det.Add(DashForward())
	h.push(6, 0)
	h.push(5, 0)
	h.push(6, 0)

	r := h.detect(true)
	if len(r) != 1 || r[0].ID != "DASH_F" {
		t.Fatalf("识别=%v 期望 [DASH_F]", ids(r))
	}
}

func TestDashForward_HoldingForward_NoRetrigger(t *testing.T) {
	h := newHarness()
	h.det.Add(DashForward())
	h.push(6, 0)
	h.push(5, 0)
	h.push(6, 0)
	h.detect(true) // 第一次识别
	h.push(6, 0)   // 继续按住前，末步非"刚进入"

	if len(h.detect(true)) != 0 {
		t.Fatal("按住前不得重复触发冲刺")
	}
}

func TestDashForward_WithoutRelease_Rejected(t *testing.T) {
	h := newHarness()
	h.det.Add(DashForward())
	h.push(5, 0)
	h.push(6, 0)
	h.push(6, 0)
	h.push(6, 0)

	if len(h.detect(true)) != 0 {
		t.Fatal("无中间松开步不构成双击")
	}
}

func ids(ps []*Pattern) []string {
	out := make([]string, len(ps))
	for i, p := range ps {
		out[i] = p.ID
	}
	return out
}
