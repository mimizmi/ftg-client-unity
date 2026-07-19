package content

import (
	"os"
	"testing"

	"ftgserver/sim/combat"
)

// 角色定义夹具的装载验证。夹具由 Unity 菜单 FG/导出角色定义夹具 生成
// （server/testdata/frank_definition.pb，进 git）；缺失时跳过并提示导出。

const frankFixture = "../../testdata/frank_definition.pb"

func TestLoadDefinition_Frank(t *testing.T) {
	if _, err := os.Stat(frankFixture); err != nil {
		t.Skipf("夹具缺失（%s）：请在 Unity 里运行菜单 FG/导出角色定义夹具 后重跑", frankFixture)
	}

	def, err := LoadDefinition(frankFixture)
	if err != nil {
		t.Fatalf("装载失败: %v", err)
	}
	if def.CharacterID != "Frank" {
		t.Fatalf("CharacterId=%q 期望 Frank", def.CharacterID)
	}

	// 结构量级断言（对齐 FighterDefinition.cs 的 BuildShoto）
	if len(def.Motions) != 3 {
		t.Errorf("Motions=%d 期望 3（623P/DASH_F/DASH_B）", len(def.Motions))
	}
	if len(def.MoveEntries) != 7 {
		t.Errorf("MoveEntries=%d 期望 7", len(def.MoveEntries))
	}
	if len(def.Moves) < 15 {
		t.Errorf("Moves=%d 期望 ≥15", len(def.Moves))
	}
	if def.Movement == nil || def.Movement.IdleID == "" {
		t.Error("Movement 配置缺失")
	}
	if len(def.ReactionMoves) != 6 {
		t.Errorf("ReactionMoves=%d 期望 6", len(def.ReactionMoves))
	}

	// 数值抽查：站立轻拳 5/3/10、30 伤害、StandLight
	var lp *combat.MoveData
	for _, m := range def.Moves {
		if m.MoveID == "Frank_FS4_Attack_Punch_L_02" {
			lp = m
			break
		}
	}
	if lp == nil {
		t.Fatal("找不到 Frank_FS4_Attack_Punch_L_02")
	}
	if lp.Startup != 5 || lp.Active != 3 || lp.Recovery != 10 {
		t.Errorf("轻拳帧数据 %d/%d/%d 期望 5/3/10", lp.Startup, lp.Active, lp.Recovery)
	}
	if lp.Damage != 30 || lp.Reaction != combat.ReactionStandLight {
		t.Errorf("轻拳 Damage=%d Reaction=%d", lp.Damage, lp.Reaction)
	}

	// 与 JSON 管线拼接：注入判定框/位移后，走路招应有位移
	boxes, err := LoadBoxes(repoPath("Assets/BoxData/Frank_boxes.json"))
	if err != nil {
		t.Fatalf("boxes 加载失败: %v", err)
	}
	rm, err := LoadRootMotion(repoPath("Assets/BoxData/Frank_rootmotion.json"))
	if err != nil {
		t.Fatalf("rootmotion 加载失败: %v", err)
	}
	Apply(def.Moves, boxes, rm)

	for _, m := range def.Moves {
		if m.MoveID == def.Movement.WalkForwardID {
			if len(m.RootMotion) == 0 {
				t.Error("前行走招注入后应有位移")
			}
			if !m.HasBoxes(combat.BoxPush) {
				t.Error("前行走招注入后应有推挡框")
			}
		}
	}
}
