package lockstep

import (
	"fmt"

	"ftgserver/sim/combat"
	"ftgserver/sim/content"
	"ftgserver/sim/input"
	"ftgserver/sim/seat"
	"ftgserver/sim/statehash"
)

// 回滚网络（N5-② rollback）。相对帧同步（lockstep 需等对方输入到齐才推进，延迟直接变卡顿），
// 回滚让【本地输入立即生效】（inputDelay 可为 0），远端未到的输入用"预测"（重复上一帧）先跑，
// 真输入到达后若与预测不符，就【还原到确认帧的快照、用真输入重模拟】到当前帧——延迟被藏成
// 偶发的画面回跳，而非持续的输入迟滞。这是格斗游戏联机的黄金标准（GGPO 家族）。
//
// 本实现的两份模拟：
//   · confirmed：权威模拟，只用【双方真输入】按帧序推进，永不回退。它逐帧产出的 StateHash
//     即【最终确定】的哈希轨迹，必须与单机 duel.RunReplay 参照、与两端彼此逐位一致。
//   · sim（predicted）：每墙钟帧从 confirmed 深克隆（= 存档/还原）再用预测输入跑到 head，
//     供"当前画面"。深克隆走 combat.BattleSimulation.Clone，这正是回滚的存档/还原引擎。
//
// 关键正确性主张：无论预测错多少次、回滚多少帧，confirmed 轨迹恒等于单机参照——
// 回滚只改变"何时看到正确结果"，绝不改变"最终的正确结果"。StateHasher 是现成 desync 探针。

// cloneNetSeat 把一个座位深拷贝成独立新座位（回滚重放要独立的座位历史）。
// 具体类型 *seat.NetworkSeat 只在本网络层可见，combat 经回调注入、并不知情。
func cloneNetSeat(s combat.Seat) combat.Seat { return s.(*seat.NetworkSeat).Clone() }

// RollbackPeer 是回滚的一端：持权威 confirmed 模拟 + 预测 sim，管理本地/远端真输入与预测。
type RollbackPeer struct {
	confirmed *combat.BattleSimulation // 权威，仅真输入推进，永不回退
	sim       *combat.BattleSimulation // 预测（当前画面），每帧从 confirmed 克隆重建
	transport Transport
	script    Script
	localIsP1 bool
	delay     int // D：本地输入生效延迟，回滚可为 0（本地即时响应）

	wallTick   int
	prevHeld   input.ButtonMask
	realLocal  map[int]input.Frame // 本端真输入（按 sim 帧号；恒为真）
	realRemote map[int]input.Frame // 远端真输入（收包填入）
	remoteReal int                 // 已连续就位的最高远端真帧（1..remoteReal 皆真）
	localReal  int                 // 已采样的最高本地帧 = wallTick + D

	confirmedTrace []uint64       // confirmed 逐帧最终哈希（= 对拍轨迹）
	predictedHash  map[int]uint64 // 某帧【首次以预测输入】跑出的哈希，用于事后判定预测是否被修正

	// 指标（证明"回滚真的发生并修正了"）
	Corrections int // 预测哈希 ≠ 最终确认哈希的帧数（真·误预测被回滚修正）
	MaxRollback int // 单帧重模拟的最大窗口 = head − confirmedFrame（≈ 延迟深度）
}

// prime 播种输入延迟窗口：sim 帧 1..D 双方皆中立、皆视为真（两端同约定），并记为已就位。
func (p *RollbackPeer) prime() {
	neutral := input.Frame{Direction: 5}
	for f := 1; f <= p.delay; f++ {
		p.realLocal[f] = neutral
		p.realRemote[f] = neutral
	}
	p.remoteReal = p.delay
	p.localReal = p.delay
}

// Advance 走一墙钟帧：采样本地 → 收远端 → 推进 confirmed（真输入）→ 从 confirmed 重建预测 sim。
func (p *RollbackPeer) Advance() {
	// ① 采样本地输入。回滚下本地立即生效：sim 帧 = wallTick + D（D 通常 0）。
	p.wallTick++
	dir, held := p.script(p.wallTick)
	frame := input.Frame{
		Direction: dir,
		Held:      held,
		Pressed:   held &^ p.prevHeld,
		Released:  p.prevHeld &^ held,
	}
	p.prevHeld = held
	simFrame := p.wallTick + p.delay
	p.realLocal[simFrame] = frame
	p.localReal = simFrame
	p.transport.Send(InputPacket{Frame: simFrame, Input: frame})

	// ② 收远端真输入，扩展连续就位区间。
	for _, pkt := range p.transport.Drain() {
		p.realRemote[pkt.Frame] = pkt.Input
	}
	for {
		if _, ok := p.realRemote[p.remoteReal+1]; !ok {
			break
		}
		p.remoteReal++
	}

	// ③ 用双方真输入把 confirmed 尽可能往前推（这些帧从此定稿、永不回退）。
	p.advanceConfirmed()

	// ④ 从 confirmed 深克隆出预测 sim，用预测输入跑到 head（= 本地已采样的最远帧）。
	p.buildPredicted()
}

// advanceConfirmed 只要下一帧双方真输入都在，就把权威模拟推进一帧并定稿其哈希；
// 若该帧此前的预测哈希与最终哈希不同，计一次"回滚修正"。
func (p *RollbackPeer) advanceConfirmed() {
	for {
		f := p.confirmed.CurrentFrame + 1
		lin, lok := p.realLocal[f]
		rin, rok := p.realRemote[f]
		if !lok || !rok {
			break
		}
		p1in, p2in := p.assign(lin, rin)
		drive(p.confirmed, f, p1in, p2in)
		h := statehash.HashState(p.confirmed)
		p.confirmedTrace = append(p.confirmedTrace, h)
		if ph, ok := p.predictedHash[f]; ok && ph != h {
			p.Corrections++ // 预测错过、已被真输入纠正
		}
	}
}

// buildPredicted 存档/还原：从 confirmed 克隆，重模拟 confirmedFrame+1..head。
// head 之外的远端帧用"重复上一帧"预测；首次以预测跑出的帧哈希留存，供事后判定是否被修正。
func (p *RollbackPeer) buildPredicted() {
	head := p.localReal
	base := p.confirmed.CurrentFrame
	if window := head - base; window > p.MaxRollback {
		p.MaxRollback = window
	}
	p.sim = p.confirmed.Clone(cloneNetSeat)
	for f := base + 1; f <= head; f++ {
		lin := p.realLocal[f] // head 内本地帧必在
		rin, real := p.realRemote[f]
		if !real {
			rin = p.predictRemote() // 预测：重复最后一个已知远端输入
		}
		p1in, p2in := p.assign(lin, rin)
		drive(p.sim, f, p1in, p2in)
		if !real {
			if _, seen := p.predictedHash[f]; !seen {
				p.predictedHash[f] = statehash.HashState(p.sim)
			}
		}
	}
}

// predictRemote 重复最后一个连续就位的远端真输入（重复上一帧是最简且实战有效的预测器）。
func (p *RollbackPeer) predictRemote() input.Frame {
	if p.remoteReal == 0 {
		return input.Frame{Direction: 5}
	}
	return p.realRemote[p.remoteReal]
}

// assign 按本端所在座位把 (本地,远端) 输入映射到 (P1,P2)。
func (p *RollbackPeer) assign(local, remote input.Frame) (p1, p2 input.Frame) {
	if p.localIsP1 {
		return local, remote
	}
	return remote, local
}

// ConfirmedFrame/HeadFrame/ConfirmedTrace/Sim 暴露给测试与展示层。
func (p *RollbackPeer) ConfirmedFrame() int           { return p.confirmed.CurrentFrame }
func (p *RollbackPeer) HeadFrame() int                { return p.sim.CurrentFrame }
func (p *RollbackPeer) ConfirmedTrace() []uint64      { return p.confirmedTrace }
func (p *RollbackPeer) Sim() *combat.BattleSimulation { return p.sim }

// drive 用给定双方输入把某模拟推进一帧（前置：sim.CurrentFrame == frame-1）。
// 座位具体类型只在本层可见，故在此就地 Confirm。
func drive(sim *combat.BattleSimulation, frame int, p1in, p2in input.Frame) {
	sim.P1.Seat().(*seat.NetworkSeat).Confirm(frame, p1in)
	sim.P2.Seat().(*seat.NetworkSeat).Confirm(frame, p2in)
	sim.Tick()
}

// RollbackMatch 是进程内两端回滚对局夹具：A 控 P1、B 控 P2，各持权威+预测两份模拟，
// 经带延迟链路交换真输入。用它证明：无论预测/回滚多频繁，两端 confirmed 轨迹逐位一致、
// 且等于单机参照——同时统计确有预测被修正（回滚真的在工作）。
type RollbackMatch struct {
	A, B       *RollbackPeer
	aToB, bToA *Pipe
}

// PeerConfig 装配单个回滚端所需的一切。真实产品里一个客户端进程 = 一个 RollbackPeer +
// 一个走 UDP 的 Transport；进程内对局夹具（RollbackMatch）则给它注入 Pipe Transport。
type PeerConfig struct {
	P1Def, P2Def *combat.FighterDefinition
	Config       *combat.BattleConfig
	Transport    Transport // 输入信道：进程内 Pipe 或线上 UDP，peer 逻辑不区分
	Script       Script    // 本端设备采样
	LocalIsP1    bool      // 本端占 P1（true）还是 P2（false）座位
	InputDelay   int       // D：回滚通常 0
}

// NewRollbackPeer 装配一个回滚端：建【完整】双人确定性模拟、播种输入延迟窗口、克隆初始预测。
// 两端各自持一份，差异仅在 LocalIsP1（谁自采谁收包）与所接 Transport。
func NewRollbackPeer(cfg PeerConfig) *RollbackPeer {
	config := cfg.Config
	if config == nil {
		config = combat.NewBattleConfig()
	}
	sp1, sp2 := seat.NewNetworkSeat(), seat.NewNetworkSeat()
	f1 := content.BuildFighter(cfg.P1Def, sp1, SpawnP1, "P1")
	f2 := content.BuildFighter(cfg.P2Def, sp2, SpawnP2, "P2")
	sim := combat.NewBattleSimulation(f1, f2, combat.NewCollisionResolver(), config)

	p := &RollbackPeer{
		confirmed:     sim,
		transport:     cfg.Transport,
		script:        cfg.Script,
		localIsP1:     cfg.LocalIsP1,
		delay:         cfg.InputDelay,
		realLocal:     make(map[int]input.Frame),
		realRemote:    make(map[int]input.Frame),
		predictedHash: make(map[int]uint64),
	}
	p.prime()
	p.sim = p.confirmed.Clone(cloneNetSeat) // 初始预测 = 空局克隆
	return p
}

// NewRollbackMatch 装配进程内两端对局夹具，经带延迟 Pipe 链路交换输入。
func NewRollbackMatch(cfg MatchConfig) *RollbackMatch {
	tA, tB, aToB, bToA := NewLinkPair(cfg.Latency)
	base := PeerConfig{P1Def: cfg.P1Def, P2Def: cfg.P2Def, Config: cfg.Config, InputDelay: cfg.InputDelay}

	aCfg := base
	aCfg.Transport, aCfg.Script, aCfg.LocalIsP1 = tA, cfg.P1Script, true
	bCfg := base
	bCfg.Transport, bCfg.Script, bCfg.LocalIsP1 = tB, cfg.P2Script, false

	return &RollbackMatch{
		A:    NewRollbackPeer(aCfg),
		B:    NewRollbackPeer(bCfg),
		aToB: aToB, bToA: bToA,
	}
}

// Step 走一墙钟帧：两端各 Advance，再让两条链路各推进一格时间投递到期包。
func (m *RollbackMatch) Step() {
	m.A.Advance()
	m.B.Advance()
	m.aToB.Step()
	m.bToA.Step()
}

// RunFrames 驱动到两端【确认帧】都 ≥ target；安全上限内未达成则报错。
func (m *RollbackMatch) RunFrames(target int) error {
	limit := target + 2*(m.aToB.latency+m.A.delay) + 16
	for range limit {
		if m.A.ConfirmedFrame() >= target && m.B.ConfirmedFrame() >= target {
			return nil
		}
		m.Step()
	}
	return fmt.Errorf("回滚未在 %d 墙钟帧内确认到目标 %d（A=%d B=%d）",
		limit, target, m.A.ConfirmedFrame(), m.B.ConfirmedFrame())
}
