package lockstep

import (
	"fmt"

	"ftgserver/sim/combat"
	"ftgserver/sim/content"
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
	"ftgserver/sim/seat"
	"ftgserver/sim/statehash"
)

// 出生点必须与 duel.RunReplay / C# BattleBootstrap 一致，否则首帧起点即分叉。
// （值刻意与 duel.SpawnP1/P2 相同；对拍/参照测试会立刻抓出任何漂移。）
var (
	SpawnP1 = fixed.Vec2FromFloat(-1, 0)
	SpawnP2 = fixed.Vec2FromFloat(1, 0)
)

// Script 是本端设备采样：给定墙钟帧号（本端第几次采样），返回该帧的方向与按住键。
// 边沿（pressed/released）由 Peer 在采样流上推导，与真实座位一致——调用方只描述"按住了什么"。
type Script func(wallTick int) (dir uint8, held input.ButtonMask)

// Peer 是帧同步的一端：持一份【完整】确定性 BattleSimulation，本端玩家的输入自采并延迟 D 帧
// 生效（藏住网络往返），远端玩家的输入靠 Transport 收包填入。某一 sim 帧只有当双方座位都
// Confirmed 时才 Tick——这把"两端看到完全相同的确定性演化"变成硬约束。
type Peer struct {
	sim        *combat.BattleSimulation
	localSeat  *seat.NetworkSeat // 本端玩家（自采 + 延迟）
	remoteSeat *seat.NetworkSeat // 远端玩家（收包）
	transport  Transport
	script     Script
	inputDelay int

	wallTick int              // 本端已采样的墙钟帧数
	prevHeld input.ButtonMask // 上一采样帧按住键，用于推导 pressed 边沿
	trace    []uint64         // 逐 sim 帧 StateHash（desync 探针 / 对拍轨迹）
}

// prime 把 sim 帧 1..D 预置为中立（输入延迟缓冲初始为空）。两端对【双方】座位都这样做，
// 约定一致：延迟窗口内谁都还没真正操作。这些帧不走网络（双方按约定已知）。
func (p *Peer) prime() {
	neutral := input.Frame{Direction: 5}
	for f := 1; f <= p.inputDelay; f++ {
		p.localSeat.Confirm(f, neutral)
		p.remoteSeat.Confirm(f, neutral)
	}
}

// Advance 走一墙钟帧：① 采样本端输入 → 确认到 sim 帧 wallTick+D 并发出；
// ② 收取远端到达的包填入远端座位；③ 只要下一 sim 帧双方都确认就推进（可能一次推进 0..多帧）。
func (p *Peer) Advance() {
	// ① 本端采样：墙钟帧 w 的操作在 D 帧后（sim 帧 w+D）生效，给网络包 D 帧的传输窗口。
	p.wallTick++
	dir, held := p.script(p.wallTick)
	frame := input.Frame{
		Direction: dir,
		Held:      held,
		Pressed:   held &^ p.prevHeld,
		Released:  p.prevHeld &^ held,
	}
	p.prevHeld = held
	simFrame := p.wallTick + p.inputDelay
	p.localSeat.Confirm(simFrame, frame)
	p.transport.Send(InputPacket{Frame: simFrame, Input: frame})

	// ② 收远端输入。
	for _, pkt := range p.transport.Drain() {
		p.remoteSeat.Confirm(pkt.Frame, pkt.Input)
	}

	// ③ 双方都确认才推进——帧同步的核心闸门。
	for p.canStep() {
		p.sim.Tick()
		p.trace = append(p.trace, statehash.HashState(p.sim))
	}
}

// canStep 报告下一 sim 帧的双方输入是否都已就位。
func (p *Peer) canStep() bool {
	next := p.sim.CurrentFrame + 1
	return p.localSeat.Confirmed(next) && p.remoteSeat.Confirmed(next)
}

// SimFrame 是本端已确定推进到的 sim 帧号。
func (p *Peer) SimFrame() int { return p.sim.CurrentFrame }

// Trace 返回逐帧 StateHash 轨迹（只读）。两端的 Trace 必须逐位一致，否则即 desync。
func (p *Peer) Trace() []uint64 { return p.trace }

// Sim 暴露底层模拟供检视（血量/位置等断言）。
func (p *Peer) Sim() *combat.BattleSimulation { return p.sim }

// MatchConfig 描述一场帧同步对局：双方角色定义、双方脚本、输入延迟 D、单程延迟 L、战斗配置。
type MatchConfig struct {
	P1Def, P2Def *combat.FighterDefinition
	P1Script     Script
	P2Script     Script
	InputDelay   int // D：本端输入延迟生效的帧数（需 ≥ 单程延迟才不落后）
	Latency      int // L：链路单程整数帧延迟
	Config       *combat.BattleConfig
}

// Match 是进程内的两端帧同步夹具：A 控 P1、B 控 P2，各跑一份 sim，经带延迟链路交换输入。
// 这是"两台机器打同一局"的可复现替身——用它证明两端逐帧逐位一致、且与单机参照一致。
type Match struct {
	A, B       *Peer
	aToB, bToA *Pipe
}

// NewMatch 装配两端。A/B 各自的 sim 用同一份 def、同一出生点、同一配置——
// 唯一差异是"谁的输入自采、谁的输入收包"，这正是帧同步要抹平的差异。
func NewMatch(cfg MatchConfig) *Match {
	config := cfg.Config
	if config == nil {
		config = combat.NewBattleConfig()
	}
	tA, tB, aToB, bToA := NewLinkPair(cfg.Latency)

	buildSim := func() (*combat.BattleSimulation, *seat.NetworkSeat, *seat.NetworkSeat) {
		sp1, sp2 := seat.NewNetworkSeat(), seat.NewNetworkSeat()
		p1 := content.BuildFighter(cfg.P1Def, sp1, SpawnP1, "P1")
		p2 := content.BuildFighter(cfg.P2Def, sp2, SpawnP2, "P2")
		sim := combat.NewBattleSimulation(p1, p2, combat.NewCollisionResolver(), config)
		return sim, sp1, sp2
	}

	simA, a1, a2 := buildSim()
	peerA := &Peer{
		sim: simA, localSeat: a1, remoteSeat: a2,
		transport: tA, script: cfg.P1Script, inputDelay: cfg.InputDelay,
	}
	peerA.prime()

	simB, b1, b2 := buildSim()
	peerB := &Peer{
		sim: simB, localSeat: b2, remoteSeat: b1,
		transport: tB, script: cfg.P2Script, inputDelay: cfg.InputDelay,
	}
	peerB.prime()

	return &Match{A: peerA, B: peerB, aToB: aToB, bToA: bToA}
}

// Step 走一墙钟帧：两端各 Advance（先都收发+推进），再让两条链路各推进一格时间（投递到期包）。
// 两端都在投递前 Drain，保证对称。
func (m *Match) Step() {
	m.A.Advance()
	m.B.Advance()
	m.aToB.Step()
	m.bToA.Step()
}

// RunFrames 驱动到两端 sim 帧都 ≥ target；若在安全上限内仍未达成（丢包/延迟过大导致锁死），报错。
// safety 上限 = target + 2*(L + D) + 8，覆盖启动灌注与延迟窗口。
func (m *Match) RunFrames(target int) error {
	limit := target + 2*(m.aToB.latency+m.A.inputDelay) + 8
	for range limit {
		if m.A.SimFrame() >= target && m.B.SimFrame() >= target {
			return nil
		}
		m.Step()
	}
	return fmt.Errorf("帧同步未在 %d 墙钟帧内达到目标 %d（A=%d B=%d）：疑似锁死/延迟>D",
		limit, target, m.A.SimFrame(), m.B.SimFrame())
}
