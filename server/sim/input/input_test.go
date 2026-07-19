package input

import "testing"

// 输入层基础件的 Go 镜像，对齐 C# InputCoreTests：环形缓冲、Numpad 记法、指令缓冲队列。

func TestBuffer_WrapsAround_KeepsNewest(t *testing.T) {
	b := NewBuffer(4)
	for i := 1; i <= 6; i++ {
		b.Push(Frame{Frame: i, Direction: uint8(i % 10)})
	}
	if b.Count() != 4 {
		t.Fatalf("Count=%d 期望 4", b.Count())
	}
	if b.Latest().Frame != 6 {
		t.Fatalf("Latest.Frame=%d 期望 6", b.Latest().Frame)
	}
	oldest, ok := b.TryGet(3)
	if !ok || oldest.Frame != 3 {
		t.Errorf("TryGet(3)=%d,%v 期望 3,true（容量 4 只留最近 4 帧）", oldest.Frame, ok)
	}
	if _, ok := b.TryGet(4); ok {
		t.Error("TryGet(4) 应报 false（越过保留范围）")
	}
}

func TestBuffer_Clear_Empties(t *testing.T) {
	b := NewBuffer(8)
	b.Push(Frame{Frame: 1})
	b.Clear()
	if b.Count() != 0 {
		t.Errorf("Count=%d 期望 0", b.Count())
	}
	if _, ok := b.TryGet(0); ok {
		t.Error("清空后 TryGet(0) 应为 false")
	}
}

func TestNumpad_FromAxes_MapsAllNine(t *testing.T) {
	cases := []struct {
		dx, dy int
		want   uint8
	}{
		{-1, -1, 1}, {0, -1, 2}, {1, -1, 3},
		{-1, 0, 4}, {0, 0, 5}, {1, 0, 6},
		{-1, 1, 7}, {0, 1, 8}, {1, 1, 9},
	}
	for _, c := range cases {
		if got := FromAxes(c.dx, c.dy); got != c.want {
			t.Errorf("FromAxes(%d,%d)=%d 期望 %d", c.dx, c.dy, got, c.want)
		}
	}
}

func TestNumpad_Mirror(t *testing.T) {
	cases := map[uint8]uint8{1: 3, 4: 6, 7: 9, 6: 4, 2: 2, 5: 5, 8: 8}
	for in, want := range cases {
		if got := Mirror(in); got != want {
			t.Errorf("Mirror(%d)=%d 期望 %d", in, got, want)
		}
	}
}

func TestCommandQueue_ExpiresAfterBufferFrames(t *testing.T) {
	q := &CommandQueue{BufferFrames: 8}
	q.Enqueue("QCF_P", 100, 10) // Expire = 18

	q.Tick(18)
	if q.Count() != 1 {
		t.Error("到期帧当帧仍有效")
	}
	q.Tick(19)
	if q.Count() != 0 {
		t.Error("过期后应被移除")
	}
}

func TestCommandQueue_ReEnqueue_RefreshesExpiry(t *testing.T) {
	q := &CommandQueue{BufferFrames: 8}
	q.Enqueue("QCF_P", 100, 10)
	q.Enqueue("QCF_P", 100, 15) // 同名刷新，不追加
	if q.Count() != 1 {
		t.Fatalf("Count=%d 期望 1（同名刷新）", q.Count())
	}
	q.Tick(20)
	if q.Count() != 1 {
		t.Error("过期时间应按第二次入队 15+8=23 计算")
	}
}

func TestCommandQueue_ConsumesBestPriority(t *testing.T) {
	q := &CommandQueue{BufferFrames: 8}
	q.Enqueue("QCF_P", 100, 10)
	q.Enqueue("DP_P", 110, 10)

	best, ok := q.TryConsume(nil)
	if !ok || best.ID != "DP_P" {
		t.Fatalf("消费=%v 期望 DP_P（高优先先消费）", best)
	}
	if q.Count() != 1 {
		t.Errorf("Count=%d 期望 1", q.Count())
	}
	rem, ok := q.TryPeek(nil)
	if !ok || rem.ID != "QCF_P" {
		t.Errorf("剩余=%v 期望 QCF_P", rem)
	}
}

func TestCommandQueue_TryPeek_DoesNotRemove(t *testing.T) {
	q := &CommandQueue{BufferFrames: 8}
	q.Enqueue("QCF_P", 100, 10)
	if _, ok := q.TryPeek(nil); !ok {
		t.Error("TryPeek 应命中")
	}
	if q.Count() != 1 {
		t.Error("TryPeek 不应移除")
	}
}
