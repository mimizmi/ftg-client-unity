// Command client 是 N6 的无头机器人客户端：Dial 中继服务器、握手拿座位与权威开局头、
// 本地跑 N5 回滚引擎，按 60Hz 采样脚本输入并与对家逐帧对齐。可开两个进程 + 一个服务器，
// 观察两端 confirmed 帧同步推进、哈希一致。
//
// 用法（三个终端）：
//
//	go run ./cmd/server -addr :7777
//	go run ./cmd/client -server 127.0.0.1:7777 -frames 600
//	go run ./cmd/client -server 127.0.0.1:7777 -frames 600
package main

import (
	"flag"
	"log"
	"time"

	ftgv1 "ftgserver/gen/ftg/v1"
	"ftgserver/netcode"
	"ftgserver/sim/content"
	"ftgserver/sim/input"
	"ftgserver/sim/lockstep"
)

func main() {
	server := flag.String("server", "127.0.0.1:7777", "服务器 UDP 地址")
	defPath := flag.String("def", "testdata/frank_definition.pb", "角色定义夹具")
	boxesPath := flag.String("boxes", "../Assets/BoxData/Frank_boxes.json", "判定框 JSON")
	rmPath := flag.String("rootmotion", "../Assets/BoxData/Frank_rootmotion.json", "位移 JSON")
	frames := flag.Int("frames", 600, "对局帧数（60Hz，600=10 秒）")
	flag.Parse()

	def, err := content.LoadCharacter(*defPath, *boxesPath, *rmPath)
	if err != nil {
		log.Fatalf("装载角色失败：%v", err)
	}

	ct, err := netcode.Dial(*server, &ftgv1.JoinRequest{
		MatchId: "m1", CharacterId: "Frank", ProtocolVersion: 1,
	}, 32)
	if err != nil {
		log.Fatalf("连接服务器失败：%v", err)
	}
	defer ct.Close()

	log.Printf("已连接 %s，等待对家…", *server)
	if err := ct.WaitReady(30 * time.Second); err != nil {
		log.Fatalf("等待对局就绪失败：%v", err)
	}
	seat := ct.Seat()
	log.Printf("对局就绪：本端座位 P%d", seat)

	peer := lockstep.NewRollbackPeer(lockstep.PeerConfig{
		P1Def: def, P2Def: def, Transport: ct,
		Script: scriptForSeat(seat), LocalIsP1: seat == 1,
	})

	// 依实测 RTT 推荐输入延迟（观测用；实机在开局/局间采用，见 DelayController 注释的确定性纪律）。
	delayCtl := lockstep.NewDelayController(0, 8)

	// 60Hz 驱动，直到确认帧达到目标。
	ticker := time.NewTicker(time.Second / 60)
	defer ticker.Stop()
	for range ticker.C {
		peer.Advance()
		recD := delayCtl.Observe(ct.Stats().RttFrames)
		if f := peer.ConfirmedFrame(); f%60 == 0 && f > 0 {
			s := ct.Stats()
			log.Printf("[%s] 确认帧 %d（预测头 %d，修正 %d，最大回滚 %d 帧；RTT≈%d 帧，新鲜度 %d 步，推荐输入延迟 %d 帧）",
				ct.State(), f, peer.HeadFrame(), peer.Corrections, peer.MaxRollback, s.RttFrames, s.StaleSteps, recD)
		}
		if peer.ConfirmedFrame() >= *frames {
			break
		}
	}
	tr := peer.ConfirmedTrace()
	log.Printf("对局结束：确认 %d 帧，末帧哈希 %016x（与对家逐位一致即同步成功）",
		len(tr), tr[len(tr)-1])
}

// scriptForSeat：P1 前进逼近后连点 LP；P2 中途下蹲、也连点 LP。两端由服务器分配座位后各取一条。
func scriptForSeat(seat int) lockstep.Script {
	if seat == 1 {
		return func(w int) (uint8, input.ButtonMask) {
			switch {
			case w <= 30:
				return 6, 0
			case w%6 == 0:
				return 5, input.LP
			default:
				return 5, 0
			}
		}
	}
	return func(w int) (uint8, input.ButtonMask) {
		switch {
		case w >= 15 && w <= 25:
			return 2, 0
		case w%7 == 0:
			return 5, input.LP
		default:
			return 5, 0
		}
	}
}
