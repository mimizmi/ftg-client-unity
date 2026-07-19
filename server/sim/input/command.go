package input

// DetectedCommand 是一条已识别的搓招指令（带缓冲过期），对齐 C# CommandQueue.cs。
type DetectedCommand struct {
	ID            string
	Priority      int
	DetectedFrame int
	ExpireFrame   int
}

// CommandQueue 是搓招指令缓冲队列，对齐 C# CommandQueue：
// 同名刷新不追加、按帧过期、消费/窥视取最高优先级（同优先取更晚检出）。
type CommandQueue struct {
	BufferFrames int
	pending      []*DetectedCommand
}

func NewCommandQueue() *CommandQueue { return &CommandQueue{BufferFrames: 8} }

func (q *CommandQueue) Count() int { return len(q.pending) }

func (q *CommandQueue) Enqueue(id string, priority, currentFrame int) {
	for _, c := range q.pending {
		if c.ID == id {
			c.DetectedFrame = currentFrame
			c.ExpireFrame = currentFrame + q.BufferFrames
			return
		}
	}
	q.pending = append(q.pending, &DetectedCommand{
		ID:            id,
		Priority:      priority,
		DetectedFrame: currentFrame,
		ExpireFrame:   currentFrame + q.BufferFrames,
	})
}

// Tick 移除已过期项：ExpireFrame < currentFrame 才丢（到期帧当帧仍有效）。
func (q *CommandQueue) Tick(currentFrame int) {
	kept := q.pending[:0]
	for _, c := range q.pending {
		if c.ExpireFrame >= currentFrame {
			kept = append(kept, c)
		}
	}
	q.pending = kept
}

func (q *CommandQueue) Clear() { q.pending = q.pending[:0] }

// TryConsume 取最高优先级（同优先取更晚检出）的匹配项并移除。
func (q *CommandQueue) TryConsume(filter func(*DetectedCommand) bool) (*DetectedCommand, bool) {
	best := -1
	var cmd *DetectedCommand
	for i, c := range q.pending {
		if filter != nil && !filter(c) {
			continue
		}
		if cmd == nil || c.Priority > cmd.Priority ||
			(c.Priority == cmd.Priority && c.DetectedFrame > cmd.DetectedFrame) {
			cmd = c
			best = i
		}
	}
	if best < 0 {
		return nil, false
	}
	q.pending = append(q.pending[:best], q.pending[best+1:]...)
	return cmd, true
}

// TryPeek 同 TryConsume 的选取规则，但不移除。
func (q *CommandQueue) TryPeek(filter func(*DetectedCommand) bool) (*DetectedCommand, bool) {
	var cmd *DetectedCommand
	for _, c := range q.pending {
		if filter != nil && !filter(c) {
			continue
		}
		if cmd == nil || c.Priority > cmd.Priority ||
			(c.Priority == cmd.Priority && c.DetectedFrame > cmd.DetectedFrame) {
			cmd = c
		}
	}
	return cmd, cmd != nil
}
