package input

// Buffer 是逐帧输入历史环形缓冲，对齐客户端
// Assets/Domain/Infrastructure/Input/InputBuffer.cs。
// framesAgo=0 是最新帧；越出保留范围 TryGet 报 false（不返回脏数据）。
type Buffer struct {
	frames []Frame
	head   int // 最新元素下标；-1 = 空
	count  int
}

func NewBuffer(capacity int) *Buffer {
	return &Buffer{frames: make([]Frame, capacity), head: -1}
}

func (b *Buffer) Capacity() int { return len(b.frames) }
func (b *Buffer) Count() int    { return b.count }

// Latest 空缓冲返回零值（对齐 C# 的 default 防御）。
func (b *Buffer) Latest() Frame {
	if b.head < 0 {
		return Frame{}
	}
	return b.frames[b.head]
}

func (b *Buffer) Push(f Frame) {
	b.head = (b.head + 1) % len(b.frames)
	b.frames[b.head] = f
	if b.count < len(b.frames) {
		b.count++
	}
}

func (b *Buffer) TryGet(framesAgo int) (Frame, bool) {
	if framesAgo < 0 || framesAgo >= b.count {
		return Frame{}, false
	}
	idx := b.head - framesAgo
	if idx < 0 {
		idx += len(b.frames)
	}
	return b.frames[idx], true
}

func (b *Buffer) Clear() {
	b.head = -1
	b.count = 0
}
