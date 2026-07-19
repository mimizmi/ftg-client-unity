package combat

import (
	"testing"

	"ftgserver/sim/fixed"
)

// 判定框数据层的语义契约测试，对齐 C# 侧 BoxTrack/Box/MoveData 行为。
// 关键帧插值、ToWorld 镜像、Rect 严格不等号相交都是跨语言对拍契约。

func TestBoxTrack_Interpolation_BitExact(t *testing.T) {
	track := &BoxTrack{
		Kind:      BoxHit,
		FromFrame: 1,
		ToFrame:   5,
		Keys: []BoxKeyframe{
			{Frame: 1, X: 0, Y: 0, W: 2, H: 2},
			{Frame: 5, X: 4, Y: 0, W: 2, H: 2},
		},
	}

	// 端点前钳到首帧
	if b, ok := track.TryEvaluate(1); !ok || b.X.Raw != fixed.FromInt(0).Raw {
		t.Fatalf("frame1: ok=%v X=%d", ok, b.X.Raw)
	}
	// 中点 frame=3：t = FromFraction(2,4)=0.5 → X = Lerp(0,4,0.5)=2
	b, ok := track.TryEvaluate(3)
	if !ok || b.X.Raw != fixed.FromInt(2).Raw {
		t.Fatalf("frame3 中点插值: ok=%v X.Raw=%d 期望 %d", ok, b.X.Raw, fixed.FromInt(2).Raw)
	}
	// 端点后钳到末帧
	if b, ok := track.TryEvaluate(5); !ok || b.X.Raw != fixed.FromInt(4).Raw {
		t.Fatalf("frame5: ok=%v X=%d", ok, b.X.Raw)
	}
	// 轨道范围外
	if _, ok := track.TryEvaluate(6); ok {
		t.Fatal("frame6 应在轨道范围外")
	}
}

func TestBox_ToWorld_MirrorsByFacing(t *testing.T) {
	// X=1 的框：朝右在原点右侧 1，朝左镜像到左侧 1
	box := BoxFromFloat(1, 0, 2, 2)
	origin := fixed.Vec2FromInt(0, 0)

	right := box.ToWorld(origin, true)
	// 中心 x=1，半宽1 → [0,2]
	if right.XMin.Raw != fixed.FromInt(0).Raw || right.XMax.Raw != fixed.FromInt(2).Raw {
		t.Fatalf("朝右 XMin=%d XMax=%d", right.XMin.Raw, right.XMax.Raw)
	}
	left := box.ToWorld(origin, false)
	// 中心 x=-1，半宽1 → [-2,0]
	if left.XMin.Raw != fixed.FromInt(-2).Raw || left.XMax.Raw != fixed.FromInt(0).Raw {
		t.Fatalf("朝左 XMin=%d XMax=%d", left.XMin.Raw, left.XMax.Raw)
	}
}

func TestRect_Overlaps_StrictInequality(t *testing.T) {
	base := fixed.RectCenterSize(fixed.Zero, fixed.Zero, fixed.FromInt(2), fixed.FromInt(2)) // [-1,1]²

	// 贴边：中心距 2，右框 [1,3]，XMin=1 不 < XMax=1 → 不相交
	edge := fixed.RectCenterSize(fixed.FromInt(2), fixed.Zero, fixed.FromInt(2), fixed.FromInt(2))
	if base.Overlaps(edge) {
		t.Error("贴边不应算相交（严格不等号契约）")
	}

	// 重叠：中心距 1.5，右框 [0.5,2.5] 与 [-1,1] 相交
	over := fixed.RectCenterSize(fixed.FromFloat(1.5), fixed.Zero, fixed.FromInt(2), fixed.FromInt(2))
	if !base.Overlaps(over) {
		t.Error("重叠框应相交")
	}
}

func TestMoveData_PhaseAt_Boundaries(t *testing.T) {
	m := &MoveData{Startup: 4, Active: 3, Recovery: 5} // total 12
	cases := []struct {
		frame int
		want  MovePhase
	}{
		{0, PhaseNone},
		{1, PhaseStartup},
		{4, PhaseStartup},
		{5, PhaseActive},
		{7, PhaseActive},
		{8, PhaseRecovery},
		{12, PhaseRecovery},
		{13, PhaseNone},
	}
	for _, c := range cases {
		if got := m.PhaseAt(c.frame); got != c.want {
			t.Errorf("PhaseAt(%d)=%d 期望 %d", c.frame, got, c.want)
		}
	}
	if m.TotalFrames() != 12 {
		t.Errorf("TotalFrames=%d 期望 12", m.TotalFrames())
	}
}

func TestMoveData_CollectBoxes_FiltersByKind(t *testing.T) {
	m := &MoveData{
		BoxTracks: []*BoxTrack{
			{Kind: BoxHit, FromFrame: 1, ToFrame: 5, Keys: []BoxKeyframe{{Frame: 1, X: 1, Y: 0, W: 1, H: 1}}},
			{Kind: BoxHurt, FromFrame: 1, ToFrame: 5, Keys: []BoxKeyframe{{Frame: 1, X: 0, Y: 0, W: 1, H: 2}}},
		},
	}
	if !m.HasBoxes(BoxHit) || !m.HasBoxes(BoxHurt) || m.HasBoxes(BoxPush) {
		t.Error("HasBoxes 过滤不符")
	}

	var out []Box
	m.CollectBoxes(3, BoxHit, &out)
	if len(out) != 1 {
		t.Fatalf("Hit 框数=%d 期望 1", len(out))
	}
	m.CollectBoxes(3, BoxPush, &out)
	if len(out) != 0 {
		t.Fatalf("Push 框数=%d 期望 0", len(out))
	}
}
