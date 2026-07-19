package combat

import (
	"testing"

	"ftgserver/sim/input"
)

// 招式表解析的 Go 镜像，对齐 C# MoveTableTests：双通道取消（OnHit 连招 / Feint 变招）、
// CancelOnly、姿态过滤、优先级、指令+按键匹配。

func TestNeutral_ResolvesPlainNormal(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "", CancelNone); got != "5LP" {
		t.Errorf("=%q 期望 5LP", got)
	}
}

func TestNeutral_RejectsCancelOnly(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10})
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "TC_2", Priority: 20, CancelOnly: true, CancelFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "", CancelNone); got != "5LP" {
		t.Errorf("=%q 期望 5LP（专属段不污染中立池）", got)
	}
}

func TestNeutral_WrongButton_Nothing(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10})

	if got := tbl.ResolveButton(input.HK, StanceStanding, "", CancelNone); got != "" {
		t.Errorf("=%q 期望空", got)
	}
}

func TestOnHit_SourceInCancelFrom(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "TC_2", Priority: 20, CancelOnly: true, CancelFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "5LP", CancelOnHit); got != "TC_2" {
		t.Errorf("=%q 期望 TC_2", got)
	}
}

func TestOnHit_SourceNotInCancelFrom(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "TC_2", Priority: 20, CancelOnly: true, CancelFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "2LK", CancelOnHit); got != "" {
		t.Errorf("=%q 期望空", got)
	}
}

func TestFeint_SourceInFeintFrom(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LK, MoveID: "5LK", Priority: 10, FeintFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LK, StanceStanding, "5LP", CancelFeint); got != "5LK" {
		t.Errorf("=%q 期望 5LK", got)
	}
}

func TestFeint_MustChangeMove_SelfRestartRejected(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10, FeintFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "5LP", CancelFeint); got != "" {
		t.Errorf("=%q 期望空（变招必须变，自我重启被拒）", got)
	}
}

func TestFeint_DoesNotUseCancelFromChannel(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LK, MoveID: "5LK", Priority: 10, CancelFrom: []string{"5LP"}})

	if got := tbl.ResolveButton(input.LK, StanceStanding, "5LP", CancelFeint); got != "" {
		t.Errorf("=%q 期望空（两通道互不相干）", got)
	}
}

func TestStance_CrouchEntry(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "5LP", Priority: 10})
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "2LP", Priority: 10, Stance: StanceCrouching})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "", CancelNone); got != "5LP" {
		t.Errorf("站立=%q 期望 5LP", got)
	}
	if got := tbl.ResolveButton(input.LP, StanceCrouching, "", CancelNone); got != "2LP" {
		t.Errorf("蹲姿=%q 期望 2LP", got)
	}
}

func TestCommand_ResolvesByCommandIdAndButton(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{CommandID: "QCF_P", Buttons: input.LP, MoveID: "Fireball_L", Priority: 100})

	if got := tbl.ResolveCommand("QCF_P", input.LP, StanceStanding, "", CancelNone); got != "Fireball_L" {
		t.Errorf("=%q 期望 Fireball_L", got)
	}
	if got := tbl.ResolveCommand("QCF_P", input.HK, StanceStanding, "", CancelNone); got != "" {
		t.Errorf("=%q 期望空（按键须匹配）", got)
	}
	if got := tbl.ResolveCommand("DP_P", input.LP, StanceStanding, "", CancelNone); got != "" {
		t.Errorf("=%q 期望空（指令名不匹配）", got)
	}
}

func TestPriority_HigherWins(t *testing.T) {
	tbl := &MoveTable{}
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "Low", Priority: 1})
	tbl.Add(&MoveEntry{Buttons: input.LP, MoveID: "High", Priority: 99})

	if got := tbl.ResolveButton(input.LP, StanceStanding, "", CancelNone); got != "High" {
		t.Errorf("=%q 期望 High", got)
	}
}
