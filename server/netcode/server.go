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

	mu         sync.Mutex
	players    [3]*relayPlayer // 下标 1/2 = 座位；0 不用
	clientSeat map[string]int  // client_id → 座位（按稳定身份认人，支持重连）
	addrSeat   map[string]int  // addr.String() → 座位（输入转发按当前地址路由）
	log        func(format string, args ...any)
}

// relayPlayer 是服务器记的一名玩家：稳定身份 + 当前地址。重连即更新 addr。
type relayPlayer struct {
	clientID string
	addr     *net.UDPAddr
}

// NewRelayServer 用已监听的 UDP conn 与权威开局头构造服务器。log 可为 nil。
func NewRelayServer(conn *net.UDPConn, setup *ftgv1.MatchSetup, log func(string, ...any)) *RelayServer {
	if log == nil {
		log = func(string, ...any) {}
	}
	return &RelayServer{
		conn:       conn,
		setup:      setup,
		clientSeat: make(map[string]int),
		addrSeat:   make(map[string]int),
		log:        log,
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
			s.handleJoin(addr, body.Join.GetClientId())
		case *ftgv1.Packet_Input:
			s.handleInput(addr, body.Input)
		default:
			s.log("忽略未知包体 %T", body)
		}
	}
}

// handleJoin 按 client_id 分配/复用座位并回执权威开局头。已知 client_id 换了地址 = 重连，
// 更新其地址即可（座位不变）。ready = 双方到齐。client_id 为空时退化用地址串当身份（兼容）。
func (s *RelayServer) handleJoin(addr *net.UDPAddr, clientID string) {
	if clientID == "" {
		clientID = addr.String()
	}
	s.mu.Lock()
	seat, known := s.clientSeat[clientID]
	switch {
	case known:
		// 重连：地址变了就重新映射（旧地址路由作废，输入改发新地址）。
		if p := s.players[seat]; p.addr.String() != addr.String() {
			delete(s.addrSeat, p.addr.String())
			p.addr = addr
			s.addrSeat[addr.String()] = seat
			s.log("玩家重连：座位 %d ← 新地址 %s", seat, addr)
		}
	case s.players[1] == nil:
		seat = 1
	case s.players[2] == nil:
		seat = 2
	default:
		s.mu.Unlock()
		s.log("对局已满，拒绝 %s", addr)
		return
	}
	if !known {
		s.players[seat] = &relayPlayer{clientID: clientID, addr: addr}
		s.clientSeat[clientID] = seat
		s.addrSeat[addr.String()] = seat
		s.log("玩家加入：座位 %d ← %s", seat, addr)
	}
	ready := s.players[1] != nil && s.players[2] != nil
	var other *net.UDPAddr
	if op := s.players[3-seat]; op != nil {
		other = op.addr
	}
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
		if op := s.players[3-seat]; op != nil {
			other = op.addr
		}
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
