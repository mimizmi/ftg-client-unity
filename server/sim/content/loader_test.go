package content

import (
	"path/filepath"
	"testing"

	"ftgserver/sim/combat"
)

// 用真实的 Frank 帧数据验证装载器——Go 读的正是 C# 读的同一份 JSON。
// 结构断言（帧数、轨道类型、关键帧）+ 装入运行时 MoveData 后判定路径可用。

func repoPath(rel string) string {
	// content 包目录 = server/sim/content；仓库根在上三级。
	return filepath.Join("..", "..", "..", rel)
}

func TestLoadBoxes_Frank(t *testing.T) {
	boxes, err := LoadBoxes(repoPath("Assets/BoxData/Frank_boxes.json"))
	if err != nil {
		t.Fatalf("加载 Frank_boxes.json 失败: %v", err)
	}
	if boxes.Version != 3 || boxes.CharacterID != "Frank" {
		t.Fatalf("头部不符: Version=%d CharacterId=%q", boxes.Version, boxes.CharacterID)
	}
	if len(boxes.Moves) == 0 {
		t.Fatal("Moves 为空")
	}

	m := boxes.Find("Frank_FS4_8Way_QuickWalk_B")
	if m == nil {
		t.Fatal("找不到 Frank_FS4_8Way_QuickWalk_B")
	}
	if m.TotalFrames != 40 {
		t.Errorf("TotalFrames=%d 期望 40", m.TotalFrames)
	}
	if len(m.Tracks) != 2 {
		t.Fatalf("Tracks 数=%d 期望 2", len(m.Tracks))
	}
	// 首轨 Hurt(1)，次轨 Push(2)
	if m.Tracks[0].Kind != combat.BoxHurt || m.Tracks[1].Kind != combat.BoxPush {
		t.Errorf("轨道类型: [0]=%d [1]=%d 期望 Hurt(1)/Push(2)", m.Tracks[0].Kind, m.Tracks[1].Kind)
	}
	if len(m.Tracks[0].Keys) != 2 || m.Tracks[0].Keys[0].Frame != 1 {
		t.Errorf("首轨关键帧不符: len=%d", len(m.Tracks[0].Keys))
	}

	// 装入运行时判定路径：TryEvaluate 应能对首帧取到框（惰性烘焙成定点）
	box, ok := m.Tracks[0].TryEvaluate(1)
	if !ok {
		t.Fatal("TryEvaluate(1) 应命中")
	}
	if box.W.Raw == 0 {
		t.Error("烘焙后的框宽不应为 0")
	}
}

func TestLoadRootMotion_Frank(t *testing.T) {
	motion, err := LoadRootMotion(repoPath("Assets/BoxData/Frank_rootmotion.json"))
	if err != nil {
		t.Fatalf("加载 Frank_rootmotion.json 失败: %v", err)
	}
	if motion.CharacterID != "Frank" {
		t.Fatalf("CharacterId=%q", motion.CharacterID)
	}

	rm := motion.Find("Frank_FS4_8Way_BigStep_B")
	if rm == nil {
		t.Fatal("找不到 Frank_FS4_8Way_BigStep_B")
	}
	if rm.Frames != 45 {
		t.Errorf("Frames=%d 期望 45", rm.Frames)
	}
	if len(rm.Motion) == 0 {
		t.Fatal("Motion 为空")
	}
	// 首帧位移 x 为负（后退），y 为 0
	if rm.Motion[0].X >= 0 {
		t.Errorf("BigStep_B 首帧 x=%v 应为负（后退）", rm.Motion[0].X)
	}
	if rm.Motion[0].Y != 0 {
		t.Errorf("首帧 y=%v 应为 0", rm.Motion[0].Y)
	}
}

func TestApply_InjectsIntoMoveData(t *testing.T) {
	boxes, err := LoadBoxes(repoPath("Assets/BoxData/Frank_boxes.json"))
	if err != nil {
		t.Fatalf("加载失败: %v", err)
	}
	motion, err := LoadRootMotion(repoPath("Assets/BoxData/Frank_rootmotion.json"))
	if err != nil {
		t.Fatalf("加载失败: %v", err)
	}

	// 一个走路招（有位移、有判定框），代码侧先空壳
	walk := &combat.MoveData{MoveID: "Frank_FS4_8Way_BigStep_B"}
	Apply([]*combat.MoveData{walk}, boxes, motion)

	if len(walk.RootMotion) == 0 {
		t.Error("位移应被注入")
	}
	// BigStep_B 首帧位移 x 为负 → 定点 Raw < 0
	if walk.RootMotion[0].X.Raw >= 0 {
		t.Errorf("首帧位移 X.Raw=%d 应为负", walk.RootMotion[0].X.Raw)
	}
}
