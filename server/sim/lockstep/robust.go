package lockstep

import (
	"fmt"
	"math/rand"
)

// 网络健壮性（N6 韧性）：进程内、确定性、可复现地注入丢包/抖动/乱序，验证
// 「回滚 + 冗余窗口」在恶劣链路下 confirmed 轨迹仍逐位正确。相对 netcode 的真 UDP 测试
// （时序不可控、无法注入丢包），这套 stepped-clock 模型让每种网络条件都能一测再测、结果稳定。

// NetConditions 描述一条单程链路的恶劣程度（单位：逻辑步）。
type NetConditions struct {
	Latency  int     // 基础单程延迟
	Jitter   int     // 额外 0..Jitter 的随机延迟（制造乱序）
	LossRate float64 // 丢包概率 0..1
	Seed     int64   // 随机源种子（可复现）
}

type datagram struct {
	arriveAt int
	window   []InputPacket
	ack      int
}

// deliveredWindow 是一个已到达的数据报载荷：一段冗余窗口 + 发送端的 ack。
type deliveredWindow struct {
	window []InputPacket
	ack    int
}

// lossyPipe 是单向、带丢包/抖动的数据报管道（携带冗余窗口，非单帧）。Step 推进一格逻辑时间。
type lossyPipe struct {
	cond     NetConditions
	rng      *rand.Rand
	clock    int
	inFlight []datagram
	ready    []deliveredWindow
	// 统计
	sent       int // 发出的数据报数
	dropped    int // 丢弃的数据报数
	framesSent int // 累计发出的帧数（冗余窗口长度之和）——衡量带宽，ack 裁剪后应远小于 数据报数×W
}

func newLossyPipe(cond NetConditions, seed int64) *lossyPipe {
	return &lossyPipe{cond: cond, rng: rand.New(rand.NewSource(seed))}
}

// send 按丢包率决定丢弃，否则按 Latency+抖动排入在途队列。ack 随窗口一起过线。
func (p *lossyPipe) send(win []InputPacket, ack int) {
	p.sent++
	p.framesSent += len(win)
	if p.cond.LossRate > 0 && p.rng.Float64() < p.cond.LossRate {
		p.dropped++
		return // 整个数据报丢失（冗余窗口令被丢帧由后续报文补回）
	}
	delay := p.cond.Latency
	if p.cond.Jitter > 0 {
		delay += p.rng.Intn(p.cond.Jitter + 1)
	}
	p.inFlight = append(p.inFlight, datagram{arriveAt: p.clock + delay, window: win, ack: ack})
}

// Step 推进一格逻辑时间，把到期数据报转入可读队列（抖动会令后发先到=乱序）。
func (p *lossyPipe) Step() {
	p.clock++
	kept := p.inFlight[:0]
	for _, d := range p.inFlight {
		if d.arriveAt <= p.clock {
			p.ready = append(p.ready, deliveredWindow{window: d.window, ack: d.ack})
		} else {
			kept = append(kept, d)
		}
	}
	p.inFlight = kept
}

func (p *lossyPipe) drain() []deliveredWindow {
	out := p.ready
	p.ready = nil
	return out
}

// RedundantChannel 是带冗余窗口的 lockstep.Transport 实现（进程内版，对应线上 ClientTransport）。
// Send 把最近 W 帧作为一个数据报发出；Drain 把收到的数据报去重后吐出新帧。
type RedundantChannel struct {
	w       *Windower
	out     *lossyPipe // 本端发出方向
	in      *lossyPipe // 本端接收方向
	pending []InputPacket
}

var _ Transport = (*RedundantChannel)(nil)

// Send 记录本地输入并把冗余窗口（已按对端 ack 裁剪）+ 本端 ack 发进出向管道。
func (c *RedundantChannel) Send(p InputPacket) { c.out.send(c.w.Local(p), c.w.Ack()) }

// Drain 收取入向管道所有到达的数据报：学习对端 ack（裁剪本端重发），去重后返回新远端帧。
func (c *RedundantChannel) Drain() []InputPacket {
	for _, d := range c.in.drain() {
		c.w.RecordPeerAck(d.ack)
		c.pending = append(c.pending, c.w.Remote(d.window)...)
	}
	out := c.pending
	c.pending = nil
	return out
}

// Stats 返回本信道的连接质量快照（RTT/新鲜度/断线信号）。
func (c *RedundantChannel) Stats() ConnStats { return c.w.Stats() }

// RobustMatch 是"恶劣链路下的两端回滚对局"夹具：两 RollbackPeer 经带丢包/抖动的冗余信道交换输入。
type RobustMatch struct {
	A, B       *RollbackPeer
	chA, chB   *RedundantChannel
	aToB, bToA *lossyPipe
}

// NewRobustMatch 装配两端。windowSize=1 即退化为无冗余（用于对照：丢包即永久卡死）。
func NewRobustMatch(cfg MatchConfig, cond NetConditions, windowSize int) *RobustMatch {
	// 两个方向用不同种子，避免两向丢包/抖动完全相关。
	aToB := newLossyPipe(cond, cond.Seed)
	bToA := newLossyPipe(cond, cond.Seed+1)

	chA := &RedundantChannel{w: NewWindower(windowSize), out: aToB, in: bToA}
	chB := &RedundantChannel{w: NewWindower(windowSize), out: bToA, in: aToB}

	base := PeerConfig{P1Def: cfg.P1Def, P2Def: cfg.P2Def, Config: cfg.Config, InputDelay: cfg.InputDelay}
	aCfg := base
	aCfg.Transport, aCfg.Script, aCfg.LocalIsP1 = chA, cfg.P1Script, true
	bCfg := base
	bCfg.Transport, bCfg.Script, bCfg.LocalIsP1 = chB, cfg.P2Script, false

	return &RobustMatch{A: NewRollbackPeer(aCfg), B: NewRollbackPeer(bCfg), chA: chA, chB: chB, aToB: aToB, bToA: bToA}
}

// StatsA / StatsB 返回两端各自的连接质量快照（供测试与展示层）。
func (m *RobustMatch) StatsA() ConnStats { return m.chA.Stats() }
func (m *RobustMatch) StatsB() ConnStats { return m.chB.Stats() }

// Step 走一逻辑步：两端各 Advance，再让两条链路各推进一格时间。
func (m *RobustMatch) Step() {
	m.A.Advance()
	m.B.Advance()
	m.aToB.Step()
	m.bToA.Step()
}

// RunFrames 驱动到两端确认帧都 ≥ target。恶劣链路下确认变慢，故上限放宽。
// 达不到即报错（如无冗余下丢包永久卡死）。
func (m *RobustMatch) RunFrames(target, maxSteps int) error {
	for range maxSteps {
		if m.A.ConfirmedFrame() >= target && m.B.ConfirmedFrame() >= target {
			return nil
		}
		m.Step()
	}
	return fmt.Errorf("恶劣链路下未在 %d 步内确认到 %d 帧（A=%d B=%d；丢包 aToB=%d/%d bToA=%d/%d）",
		maxSteps, target, m.A.ConfirmedFrame(), m.B.ConfirmedFrame(),
		m.aToB.dropped, m.aToB.sent, m.bToA.dropped, m.bToA.sent)
}

// LossStats 返回两向链路的发送/丢弃计数（供测试断言确有丢包发生）。
func (m *RobustMatch) LossStats() (aSent, aDropped, bSent, bDropped int) {
	return m.aToB.sent, m.aToB.dropped, m.bToA.sent, m.bToA.dropped
}

// FramesSent 返回两向累计发出的帧数（冗余窗口长度之和）——衡量带宽。ack 裁剪生效后应远小于 数据报数×W。
func (m *RobustMatch) FramesSent() (aToB, bToA int) {
	return m.aToB.framesSent, m.bToA.framesSent
}
