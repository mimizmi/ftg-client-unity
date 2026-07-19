package input

// 回滚（N5-②）需要把整局模拟状态做深快照/还原。输入缓冲与指令队列是座位里唯二的
// 可变堆状态，这里给它们深拷贝方法——上层 seat.NetworkSeat.Clone → combat.BattleSimulation.Clone
// 靠这两个把座位历史一并快照，回滚重模拟时搓招识别的近帧历史才不丢。

// Clone 深拷贝环形缓冲：底层帧切片按值复制，head/count 一并带走。
func (b *Buffer) Clone() *Buffer {
	nf := make([]Frame, len(b.frames))
	copy(nf, b.frames) // Frame 是纯值类型，浅拷贝即深拷贝
	return &Buffer{frames: nf, head: b.head, count: b.count}
}

// Clone 深拷贝指令队列：每条 *DetectedCommand 单独复制，避免快照与现役共享可变指令
// （Enqueue 会就地改 DetectedFrame/ExpireFrame）。
func (q *CommandQueue) Clone() *CommandQueue {
	nq := &CommandQueue{BufferFrames: q.BufferFrames}
	if len(q.pending) > 0 {
		nq.pending = make([]*DetectedCommand, len(q.pending))
		for i, c := range q.pending {
			cc := *c
			nq.pending[i] = &cc
		}
	}
	return nq
}
