package netcode

import (
	"errors"
	"net"
	"sync"
	"time"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/lockstep"
)

// ClientTransport 是客户端侧的 lockstep.Transport 实现，把回滚 peer 的 Send/Drain 落到真 UDP。
// 关键：peer 逻辑对它一无所知——同一套回滚代码，进程内用 Pipe，线上用它，无缝替换。
//
// 抗丢包：每次 Send 冗余携带最近 windowSize 帧（单丢由下一报文补齐）；收端按 frame 去重，
// Drain 只吐【新】远端帧。握手：向服务器发 JoinRequest，收 JoinResponse 得知座位与权威开局头，
// 双方到齐即 ready。
type ClientTransport struct {
	conn *net.UDPConn
	join *ftgv1.JoinRequest

	mu      sync.Mutex
	seat    int
	setup   *ftgv1.MatchSetup
	ready   bool
	w       *lockstep.Windower     // 冗余窗口 + 去重（与进程内 RedundantChannel 同一实现，已被丢包测试覆盖）
	pending []lockstep.InputPacket // 待 Drain 的新远端输入
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
	ct := &ClientTransport{
		conn: conn,
		join: join,
		w:    lockstep.NewWindower(windowSize),
	}
	go ct.recvLoop()
	return ct, nil
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

// Close 关闭 conn，使接收 goroutine 退出。
func (ct *ClientTransport) Close() error { return ct.conn.Close() }

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
