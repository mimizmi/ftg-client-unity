package netcode

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"net"
	"sync"
	"time"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/lockstep"
)

// ConnectionState 是连接的宏观状态，由「远端新鲜度」StaleSteps 阈值判定（不依赖墙钟）。
type ConnectionState int

const (
	StateConnecting   ConnectionState = iota // 握手中（尚未双方到齐）
	StateConnected                           // 正常：近期有新远端输入
	StateStalled                             // 短暂断流：超警戒阈值，仍在重连尝试
	StateDisconnected                        // 长时间断流：超掉线阈值，视为掉线
)

func (s ConnectionState) String() string {
	switch s {
	case StateConnecting:
		return "连接中"
	case StateConnected:
		return "已连接"
	case StateStalled:
		return "断流中"
	default:
		return "已掉线"
	}
}

// classifyState 由是否就绪 + 新鲜度阈值判定连接状态（纯函数，便于单测）。
func classifyState(ready bool, stale, warn, dead int) ConnectionState {
	if !ready {
		return StateConnecting
	}
	switch {
	case stale > dead:
		return StateDisconnected
	case stale > warn:
		return StateStalled
	default:
		return StateConnected
	}
}

// ClientTransport 是客户端侧的 lockstep.Transport 实现，把回滚 peer 的 Send/Drain 落到真 UDP。
// 关键：peer 逻辑对它一无所知——同一套回滚代码，进程内用 Pipe，线上用它，无缝替换。
//
// 抗丢包：每次 Send 冗余携带最近 windowSize 帧（单丢由下一报文补齐）；收端按 frame 去重，
// Drain 只吐【新】远端帧。握手：向服务器发 JoinRequest，收 JoinResponse 得知座位与权威开局头，
// 双方到齐即 ready。
type ClientTransport struct {
	conn *net.UDPConn
	join *ftgv1.JoinRequest

	warnStale, deadStale int // StaleSteps 阈值：超 warn=断流、超 dead=掉线
	closeOnce            sync.Once
	done                 chan struct{}

	mu      sync.Mutex
	seat    int
	setup   *ftgv1.MatchSetup
	ready   bool
	w       *lockstep.Windower     // 冗余窗口 + 去重（与进程内 RedundantChannel 同一实现，已被丢包测试覆盖）
	pending []lockstep.InputPacket // 待 Drain 的新远端输入
}

func newClientID() string {
	var b [8]byte
	_, _ = rand.Read(b[:])
	return hex.EncodeToString(b[:])
}

var _ lockstep.Transport = (*ClientTransport)(nil)

// Dial 连接服务器并启动接收 goroutine。windowSize ≤ 0 取默认 32。
// 返回后需调 WaitReady 等对局双方到齐，再开始 Send/Drain。
func Dial(serverAddr string, join *ftgv1.JoinRequest, windowSize int) (*ClientTransport, error) {
	if windowSize <= 0 {
		windowSize = 32
	}
	raddr, err := net.ResolveUDPAddr("udp", serverAddr)
	if err != nil {
		return nil, err
	}
	conn, err := net.DialUDP("udp", nil, raddr)
	if err != nil {
		return nil, err
	}
	if join.GetClientId() == "" {
		join.ClientId = newClientID() // 传输层自持稳定身份，重连用同一 id
	}
	ct := &ClientTransport{
		conn:      conn,
		join:      join,
		w:         lockstep.NewWindower(windowSize),
		warnStale: 30,  // ~0.5s@60fps 无新远端输入 = 断流
		deadStale: 180, // ~3s 无新远端输入 = 掉线
		done:      make(chan struct{}),
	}
	go ct.recvLoop()
	go ct.heartbeatLoop()
	return ct, nil
}

// State 返回当前连接宏观状态（连接中/已连接/断流/掉线）。
func (ct *ClientTransport) State() ConnectionState {
	ct.mu.Lock()
	defer ct.mu.Unlock()
	return classifyState(ct.ready, ct.w.Stats().StaleSteps, ct.warnStale, ct.deadStale)
}

// heartbeatLoop 非「已连接」时（握手中/断流/掉线）周期性重发握手，刷新服务器映射——
// 换 socket / NAT 重绑 / 服务器重启后，凭稳定 client_id 重新落回原座位（断线重连）。
func (ct *ClientTransport) heartbeatLoop() {
	t := time.NewTicker(250 * time.Millisecond)
	defer t.Stop()
	for {
		select {
		case <-ct.done:
			return
		case <-t.C:
			if ct.State() != StateConnected {
				ct.sendJoin()
			}
		}
	}
}

// WaitReady 反复发握手直到服务器报双方到齐，或超时。UDP 不可靠，故靠重发拿到 ready。
func (ct *ClientTransport) WaitReady(timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		ct.sendJoin()
		for range 5 {
			if ct.Ready() {
				return nil
			}
			time.Sleep(10 * time.Millisecond)
		}
	}
	return errors.New("等待对局就绪超时")
}

// Seat/Setup/Ready 暴露握手结果：Seat 决定本端占 P1(1) 还是 P2(2)。
func (ct *ClientTransport) Seat() int {
	ct.mu.Lock()
	defer ct.mu.Unlock()
	return ct.seat
}

func (ct *ClientTransport) Setup() *ftgv1.MatchSetup {
	ct.mu.Lock()
	defer ct.mu.Unlock()
	return ct.setup
}

func (ct *ClientTransport) Ready() bool {
	ct.mu.Lock()
	defer ct.mu.Unlock()
	return ct.ready
}

// Send 记录本地输入并把冗余窗口（最近 W 帧）发给服务器（服务器转发对家）。
// 窗口/ack 由共享的 lockstep.Windower 维护——同一实现已被进程内丢包测试反复验证。
func (ct *ClientTransport) Send(p lockstep.InputPacket) {
	ct.mu.Lock()
	win := ct.w.Local(p)
	seat, ack := ct.seat, ct.w.Ack()
	ct.mu.Unlock()

	inputs := make([]*ftgv1.NetInput, len(win))
	for i, hp := range win {
		inputs[i] = toNetInput(hp)
	}
	dg := &ftgv1.InputDatagram{Seat: uint32(seat), Inputs: inputs, Ack: uint32(ack)}
	ct.write(&ftgv1.Packet{Body: &ftgv1.Packet_Input{Input: dg}})
}

// Drain 取走自上次以来新到的远端输入（已按 frame 去重）。
func (ct *ClientTransport) Drain() []lockstep.InputPacket {
	ct.mu.Lock()
	out := ct.pending
	ct.pending = nil
	ct.mu.Unlock()
	return out
}

// Stats 返回当前连接质量快照（RTT 帧数 / 远端新鲜度 / 断线信号）。供 cmd/client 与 C# 客户端做
// UI 显示、延迟自适应、掉线判定。
func (ct *ClientTransport) Stats() lockstep.ConnStats {
	ct.mu.Lock()
	defer ct.mu.Unlock()
	return ct.w.Stats()
}

// Close 停止心跳并关闭 conn，使接收 goroutine 退出。
func (ct *ClientTransport) Close() error {
	ct.closeOnce.Do(func() { close(ct.done) })
	return ct.conn.Close()
}

func (ct *ClientTransport) recvLoop() {
	buf := make([]byte, 4096)
	for {
		n, err := ct.conn.Read(buf)
		if err != nil {
			return
		}
		pkt, err := unmarshalPacket(buf[:n])
		if err != nil {
			continue
		}
		switch body := pkt.GetBody().(type) {
		case *ftgv1.Packet_Joined:
			ct.onJoined(body.Joined)
		case *ftgv1.Packet_Input:
			ct.onInput(body.Input)
		}
	}
}

func (ct *ClientTransport) onJoined(r *ftgv1.JoinResponse) {
	ct.mu.Lock()
	ct.seat = int(r.GetSeat())
	ct.setup = r.GetSetup()
	if r.GetReady() {
		ct.ready = true
	}
	ct.mu.Unlock()
}

func (ct *ClientTransport) onInput(dg *ftgv1.InputDatagram) {
	win := make([]lockstep.InputPacket, 0, len(dg.GetInputs()))
	for _, ni := range dg.GetInputs() {
		win = append(win, fromNetInput(ni))
	}
	ct.mu.Lock()
	ct.w.RecordPeerAck(int(dg.GetAck()))                 // 学习对端 ack，裁剪本端重发窗口（省带宽）
	ct.pending = append(ct.pending, ct.w.Remote(win)...) // 去重由共享 Windower 完成
	ct.mu.Unlock()
}

func (ct *ClientTransport) sendJoin() {
	ct.write(&ftgv1.Packet{Body: &ftgv1.Packet_Join{Join: ct.join}})
}

func (ct *ClientTransport) write(pkt *ftgv1.Packet) {
	b, err := marshalPacket(pkt)
	if err != nil {
		return
	}
	_, _ = ct.conn.Write(b)
}
