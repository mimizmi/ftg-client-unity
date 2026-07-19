package netcode

import (
	"net"
	"sync"

	ftgv1 "ftgserver/gen/ftg/v1"
)

// RelayServer 是中继权威服务器：撮合两名玩家、分配座位（P1=1/P2=2）、下发权威开局头，
// 并把每一方的输入数据报转发给另一方。它【不跑模拟】——回滚在客户端本地跑；服务器权威在于
// 它决定谁是 P1/P2、用哪份 MatchSetup 开局、以及输入的转发。真实产品可在此再挂一份校验模拟
// 做反作弊，本实现聚焦"真 socket 上的确定性回滚"。
//
// 单场对局、单 goroutine 读循环。UDP 无连接：靠客户端地址识别座位。
type RelayServer struct {
	conn  *net.UDPConn
	setup *ftgv1.MatchSetup

	mu       sync.Mutex
	seatAddr [3]*net.UDPAddr // 下标 1/2 = 座位；0 不用
	addrSeat map[string]int  // addr.String() → 座位
	log      func(format string, args ...any)
}

// NewRelayServer 用已监听的 UDP conn 与权威开局头构造服务器。log 可为 nil。
func NewRelayServer(conn *net.UDPConn, setup *ftgv1.MatchSetup, log func(string, ...any)) *RelayServer {
	if log == nil {
		log = func(string, ...any) {}
	}
	return &RelayServer{
		conn:     conn,
		setup:    setup,
		addrSeat: make(map[string]int),
		log:      log,
	}
}

// Addr 返回服务器监听地址（测试用 :0 时取实际端口）。
func (s *RelayServer) Addr() *net.UDPAddr { return s.conn.LocalAddr().(*net.UDPAddr) }

// Close 关闭底层 conn，使 Serve 的读循环退出。
func (s *RelayServer) Close() error { return s.conn.Close() }

// Serve 阻塞读循环：解析每个 UDP 包并处理握手/转发。conn 关闭即返回。
func (s *RelayServer) Serve() {
	buf := make([]byte, 4096)
	for {
		n, addr, err := s.conn.ReadFromUDP(buf)
		if err != nil {
			return // conn 关闭或致命错误：退出
		}
		pkt, err := unmarshalPacket(buf[:n])
		if err != nil {
			s.log("丢弃无法解析的包（%d 字节）：%v", n, err)
			continue
		}
		switch body := pkt.GetBody().(type) {
		case *ftgv1.Packet_Join:
			s.handleJoin(addr)
		case *ftgv1.Packet_Input:
			s.handleInput(addr, body.Input)
		default:
			s.log("忽略未知包体 %T", body)
		}
	}
}

// handleJoin 分配/复用座位并回执权威开局头。ready = 双方到齐。
func (s *RelayServer) handleJoin(addr *net.UDPAddr) {
	s.mu.Lock()
	seat, known := s.addrSeat[addr.String()]
	if !known {
		switch {
		case s.seatAddr[1] == nil:
			seat = 1
		case s.seatAddr[2] == nil:
			seat = 2
		default:
			s.mu.Unlock()
			s.log("对局已满，拒绝 %s", addr)
			return
		}
		s.seatAddr[seat] = addr
		s.addrSeat[addr.String()] = seat
		s.log("玩家加入：座位 %d ← %s", seat, addr)
	}
	ready := s.seatAddr[1] != nil && s.seatAddr[2] != nil
	other := s.seatAddr[3-seat]
	s.mu.Unlock()

	s.send(addr, &ftgv1.Packet{Body: &ftgv1.Packet_Joined{Joined: &ftgv1.JoinResponse{
		Seat: uint32(seat), Setup: s.setup, Ready: ready,
	}}})
	// 双方到齐的瞬间，主动把 ready 也推给对家（免其等下一次重发握手）。
	if ready && other != nil {
		s.send(other, &ftgv1.Packet{Body: &ftgv1.Packet_Joined{Joined: &ftgv1.JoinResponse{
			Seat: uint32(3 - seat), Setup: s.setup, Ready: true,
		}}})
	}
}

// handleInput 把发送方的输入数据报转发给对家（重新盖上服务器认定的座位号）。
func (s *RelayServer) handleInput(addr *net.UDPAddr, dg *ftgv1.InputDatagram) {
	s.mu.Lock()
	seat, known := s.addrSeat[addr.String()]
	var other *net.UDPAddr
	if known {
		other = s.seatAddr[3-seat]
	}
	s.mu.Unlock()
	if !known || other == nil {
		return // 未握手或对家未到：丢弃（客户端冗余窗口会补）
	}
	dg.Seat = uint32(seat) // 以服务器认定的座位为准
	s.send(other, &ftgv1.Packet{Body: &ftgv1.Packet_Input{Input: dg}})
}

func (s *RelayServer) send(addr *net.UDPAddr, pkt *ftgv1.Packet) {
	b, err := marshalPacket(pkt)
	if err != nil {
		s.log("序列化发送包失败：%v", err)
		return
	}
	if _, err := s.conn.WriteToUDP(b, addr); err != nil {
		s.log("发送到 %s 失败：%v", addr, err)
	}
}
