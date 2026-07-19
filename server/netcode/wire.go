// Package netcode 是 N6 真网络的线上层：把 lockstep.Transport 的进程内抽象落到真 UDP。
// RelayServer 中继权威（撮合/分座/下发开局头/转发输入），ClientTransport 是客户端侧的
// lockstep.Transport 实现——于是 N5 的回滚 peer 逻辑一行不改，直接跑在真 socket 上。
//
// wire.go：线格式编解码。proto/ftg/v1/net.proto 是跨语言唯一契约源，这里只做
// lockstep.InputPacket ↔ ftgv1.NetInput 的搬运与 Packet 的 (un)marshal。
package netcode

import (
	"google.golang.org/protobuf/proto"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/sim/input"
	"ftgserver/sim/lockstep"
)

// toNetInput 把内存输入包转成线上 NetInput（逐字保真 direction/held/pressed）。
func toNetInput(p lockstep.InputPacket) *ftgv1.NetInput {
	return &ftgv1.NetInput{
		Frame: uint32(p.Frame),
		Input: &ftgv1.Input{
			Direction: uint32(p.Input.Direction),
			Held:      uint32(p.Input.Held),
			Pressed:   uint32(p.Input.Pressed),
		},
	}
}

// fromNetInput 把线上 NetInput 还原成内存输入包。
func fromNetInput(n *ftgv1.NetInput) lockstep.InputPacket {
	in := n.GetInput()
	return lockstep.InputPacket{
		Frame: int(n.GetFrame()),
		Input: input.Frame{
			Direction: uint8(in.GetDirection()),
			Held:      input.ButtonMask(in.GetHeld()),
			Pressed:   input.ButtonMask(in.GetPressed()),
		},
	}
}

func marshalPacket(p *ftgv1.Packet) ([]byte, error) { return proto.Marshal(p) }

func unmarshalPacket(b []byte) (*ftgv1.Packet, error) {
	p := &ftgv1.Packet{}
	if err := proto.Unmarshal(b, p); err != nil {
		return nil, err
	}
	return p, nil
}
