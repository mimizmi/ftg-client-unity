package lockstep

// Windower 是 UDP 抗丢包的核心簿记：发送端把每帧的本地输入累积成【冗余窗口】（最近 W 帧），
// 接收端按帧号【去重】并追踪已连续收到的最高帧（ack）。单个数据报丢失，只要后续 W 个数据报里
// 有一个到达，被丢的帧就被补回——无需重传。这块逻辑既供进程内的确定性丢包测试（RedundantChannel），
// 又供线上 UDP 客户端（netcode.ClientTransport）复用，是"冗余+去重"的单一真源。
//
// 非并发安全：调用方（ClientTransport 在其 mutex 下）负责同步。
type Windower struct {
	windowSize int
	outHistory []InputPacket // 本端已发帧，尾部保留最近 windowSize 个
	seen       map[int]bool  // 远端帧去重
	contig     int           // 已连续收到的远端最高帧
}

// NewWindower 创建窗口器。windowSize ≤ 0 取默认 32。
func NewWindower(windowSize int) *Windower {
	if windowSize <= 0 {
		windowSize = 32
	}
	return &Windower{windowSize: windowSize, seen: make(map[int]bool)}
}

// Local 记录一帧本地输入，返回本次应发送的冗余窗口（最近 windowSize 帧的独立副本）。
func (w *Windower) Local(p InputPacket) []InputPacket {
	w.outHistory = append(w.outHistory, p)
	if len(w.outHistory) > w.windowSize {
		// 拷贝压实，避免底层数组随对局无限增长。
		w.outHistory = append([]InputPacket(nil), w.outHistory[len(w.outHistory)-w.windowSize:]...)
	}
	win := make([]InputPacket, len(w.outHistory))
	copy(win, w.outHistory)
	return win
}

// Remote 吞入一个收到的冗余窗口，返回其中【首次见到】的帧（已按帧号去重）；顺带推进 ack。
func (w *Windower) Remote(win []InputPacket) []InputPacket {
	var fresh []InputPacket
	for _, p := range win {
		if w.seen[p.Frame] {
			continue
		}
		w.seen[p.Frame] = true
		fresh = append(fresh, p)
	}
	for w.seen[w.contig+1] {
		w.contig++
	}
	return fresh
}

// Ack 返回已连续收到的远端最高帧号（供发送端裁剪窗口 / 拥塞判断）。
func (w *Windower) Ack() int { return w.contig }
