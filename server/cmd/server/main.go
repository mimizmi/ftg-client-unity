// Command server 是 N6 中继权威服务器的可执行入口：监听 UDP、撮合两名玩家、下发权威开局头、
// 转发输入。回滚在客户端本地跑（见 cmd/client 与 C# 客户端）。
//
// 用法：go run ./cmd/server -addr :7777 -char Frank
package main

import (
	"flag"
	"log"
	"net"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/netcode"
)

func main() {
	addr := flag.String("addr", ":7777", "UDP 监听地址")
	char := flag.String("char", "Frank", "对阵角色 id（镜像内战）")
	flag.Parse()

	udpAddr, err := net.ResolveUDPAddr("udp", *addr)
	if err != nil {
		log.Fatalf("解析监听地址失败：%v", err)
	}
	conn, err := net.ListenUDP("udp", udpAddr)
	if err != nil {
		log.Fatalf("监听 UDP 失败：%v", err)
	}

	setup := &ftgv1.MatchSetup{
		P1CharacterId: *char, P2CharacterId: *char,
		ProtocolVersion: 1,
		Config: &ftgv1.BattleConfig{
			RoundFrames: 99 * 60, IntroFrames: 0, RoundOverFrames: 120,
			RoundsToWin: 2, MaxHealth: 1000,
		},
	}
	srv := netcode.NewRelayServer(conn, setup, log.Printf)
	log.Printf("中继服务器就绪：%s（%s 镜像内战，等待两名玩家）", srv.Addr(), *char)
	srv.Serve() // 阻塞直到 conn 关闭
}
