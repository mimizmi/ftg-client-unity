// Package lockstep 是帧同步（N5 第一阶段）的传输无关调度器：两端各跑一份【完全相同】的
// 确定性 BattleSimulation，逐帧交换输入；某一帧只有当双方输入都到齐才推进。
// 输入延迟 D 让每端可以领先采样，把网络往返藏进这 D 帧里——只要单程延迟 ≤ D 就永不卡帧。
//
// 传输被抽象成接口：真实产品接 UDP/KCP，测试用进程内 Pipe（可注入整帧延迟/抖动）。
// 这套调度器 N5 第二阶段（回滚）继续复用——回滚只是在此之上加"预测 + 存档回退 + 重模拟"。
package lockstep

import "ftgserver/sim/input"

// InputPacket 是过线的最小单元：某绝对帧号的一个玩家输入。
// 逐字保真 direction/held/pressed（pressed 含无法从 held 推导的单帧点按位，见 combat.proto）。
type InputPacket struct {
	Frame int
	Input input.Frame
}

// Transport 是一端看到的双向输入信道：把本地输入发出去，把已到达的远端输入取回来。
// Drain 一次性取走当前所有可读包（可能乱序/成批），调度器自行按帧号归位——
// 这让实现可以是可靠有序的 TCP，也可以是需要自行排序去重的 UDP。
type Transport interface {
	Send(p InputPacket)
	Drain() []InputPacket
}

// Pipe 是进程内单向信道，latency 为整数帧延迟（0=同步到达）。Step 推进一格逻辑时间，
// 把到期的包放进可读队列。成对使用即得全双工链路（见 NewLinkPair）。
type Pipe struct {
	latency  int
	clock    int
	inFlight []timedPacket
	ready    []InputPacket
}

type timedPacket struct {
	arriveAt int
	packet   InputPacket
}

// Send 把包压入在途队列，到达时刻 = 当前时钟 + latency。
func (p *Pipe) Send(pkt InputPacket) {
	p.inFlight = append(p.inFlight, timedPacket{arriveAt: p.clock + p.latency, packet: pkt})
}

// Step 推进一格逻辑时间：把 arriveAt ≤ 新时钟的在途包转入可读队列（保持发送先后次序）。
func (p *Pipe) Step() {
	p.clock++
	kept := p.inFlight[:0]
	for _, tp := range p.inFlight {
		if tp.arriveAt <= p.clock {
			p.ready = append(p.ready, tp.packet)
		} else {
			kept = append(kept, tp)
		}
	}
	p.inFlight = kept
}

// Drain 取走并清空当前可读队列。
func (p *Pipe) Drain() []InputPacket {
	out := p.ready
	p.ready = nil
	return out
}

// endpoint 把"发到对端的 Pipe"与"从对端收的 Pipe"绑成一个 Transport 视图。
type endpoint struct {
	out *Pipe // 本端发出 → 对端读
	in  *Pipe // 对端发出 → 本端读
}

func (e *endpoint) Send(pkt InputPacket) { e.out.Send(pkt) }
func (e *endpoint) Drain() []InputPacket { return e.in.Drain() }

// NewLinkPair 造一条全双工链路，返回 A、B 两端的 Transport，与两条底层 Pipe（供测试驱动时钟）。
// latency 为单程整数帧延迟，两向对称。测试每逻辑步对两条 Pipe 各 Step 一次即模拟时间流逝。
func NewLinkPair(latency int) (a, b Transport, aToB, bToA *Pipe) {
	aToB = &Pipe{latency: latency}
	bToA = &Pipe{latency: latency}
	a = &endpoint{out: aToB, in: bToA}
	b = &endpoint{out: bToA, in: aToB}
	return a, b, aToB, bToA
}
