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
	peerAck    int           // 对端已连续收到的【本端】最高帧（从收到的报文里学得），用于裁剪重发窗口

	// 连接质量统计（不依赖墙钟，可确定性测试；给 C# 客户端做 UI 显示 / 自适应 / 断线判定铺路）
	lastLocalFrame int // 最新本地帧号
	staleSteps     int // 连续多少个本地步没收到【新】远端帧（断线信号）
}

// ConnStats 是一端看到的连接质量快照。RttFrames 是不依赖墙钟的往返延迟估计（帧为单位）：
// 本端帧 F 走单程 L 步到对端、对端 ack 再走 L 步回来，故 peerAck≈F−2L，RttFrames=F−peerAck≈2L。
type ConnStats struct {
	LocalFrame   int // 最新本地输入帧
	PeerAckFrame int // 对端已确认收到的本端最高帧
	RemoteFrame  int // 已连续收到的远端最高帧
	RttFrames    int // 往返延迟估计（帧）= LocalFrame − PeerAckFrame
	StaleSteps   int // 连续无新远端帧的本地步数（越大越可能掉线）
}

// NewWindower 创建窗口器。windowSize ≤ 0 取默认 32。
func NewWindower(windowSize int) *Windower {
	if windowSize <= 0 {
		windowSize = 32
	}
	return &Windower{windowSize: windowSize, seen: make(map[int]bool)}
}

// Local 记录一帧本地输入，返回本次应发送的冗余窗口副本。窗口 = outHistory 中【对端尚未确认】
// 的帧（Frame > peerAck），上限 windowSize 帧。已被对端 ack 的帧不再重发——省带宽；但至少发最新一帧
// （空窗口会令对端永远收不到新输入而卡死）。peerAck 陈旧（ack 丢/迟）只会多发几帧，绝不少发，故安全。
func (w *Windower) Local(p InputPacket) []InputPacket {
	w.lastLocalFrame = p.Frame
	w.staleSteps++ // 本地走一步；若本步收到新远端帧，Remote 会清零
	w.outHistory = append(w.outHistory, p)
	if len(w.outHistory) > w.windowSize {
		// 拷贝压实，避免底层数组随对局无限增长。
		w.outHistory = append([]InputPacket(nil), w.outHistory[len(w.outHistory)-w.windowSize:]...)
	}
	// 跳过已确认帧，但保底留下最新一帧。
	start := 0
	for start < len(w.outHistory)-1 && w.outHistory[start].Frame <= w.peerAck {
		start++
	}
	win := make([]InputPacket, len(w.outHistory)-start)
	copy(win, w.outHistory[start:])
	return win
}

// RecordPeerAck 记录对端已连续收到的本端最高帧（单调不减），据此裁剪后续重发窗口。
func (w *Windower) RecordPeerAck(ack int) {
	if ack > w.peerAck {
		w.peerAck = ack
	}
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
	if len(fresh) > 0 {
		w.staleSteps = 0 // 收到新远端帧：连接新鲜
	}
	return fresh
}

// Ack 返回已连续收到的远端最高帧号（供发送端裁剪窗口 / 拥塞判断）。
func (w *Windower) Ack() int { return w.contig }

// Stats 返回当前连接质量快照。
func (w *Windower) Stats() ConnStats {
	return ConnStats{
		LocalFrame:   w.lastLocalFrame,
		PeerAckFrame: w.peerAck,
		RemoteFrame:  w.contig,
		RttFrames:    w.lastLocalFrame - w.peerAck,
		StaleSteps:   w.staleSteps,
	}
}
