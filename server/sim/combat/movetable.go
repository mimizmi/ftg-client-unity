package combat

import (
	"slices"
	"sort"

	"ftgserver/sim/input"
)

// MoveTable 是角色招式表——指令/按键 → 具体招式，连招（取消/变招）规则的唯一落点，
// 对齐客户端 MoveTable.cs。解析优先级 Priority 降序，首个满足全部条件者胜出。
//
// 与 C# 的差异：MoveEntry.Condition（Func）不在此处——夹具契约要求条件以数据表达
// （见 definition.proto 头注），故 Resolve* 无需 fighter 参数。
type MoveTable struct {
	entries []*MoveEntry
}

func (t *MoveTable) Add(e *MoveEntry) {
	t.entries = append(t.entries, e)
	t.sortByPriority()
}

func (t *MoveTable) AddRange(entries []*MoveEntry) {
	t.entries = append(t.entries, entries...)
	t.sortByPriority()
}

// SliceStable 匹配 C# List.Sort 在小列表下的插入排序（稳定）行为。
func (t *MoveTable) sortByPriority() {
	sort.SliceStable(t.entries, func(i, j int) bool {
		return t.entries[i].Priority > t.entries[j].Priority
	})
}

// ResolveCommand 用搓招指令解析招式。cancelSource=="" 且 cancelKind=CancelNone 表示中立态出招。
func (t *MoveTable) ResolveCommand(commandID string, pressed input.ButtonMask,
	stance Stance, cancelSource string, cancelKind CancelKind) string {
	for _, e := range t.entries {
		if e.CommandID != commandID {
			continue
		}
		if !matchEntry(e, pressed, stance, cancelSource, cancelKind) {
			continue
		}
		return e.MoveID
	}
	return ""
}

// ResolveButton 用裸按键解析普通技（仅无 CommandID 的条目）。
func (t *MoveTable) ResolveButton(pressed input.ButtonMask,
	stance Stance, cancelSource string, cancelKind CancelKind) string {
	for _, e := range t.entries {
		if e.CommandID != "" {
			continue
		}
		if !matchEntry(e, pressed, stance, cancelSource, cancelKind) {
			continue
		}
		return e.MoveID
	}
	return ""
}

func matchEntry(e *MoveEntry, pressed input.ButtonMask,
	stance Stance, cancelSource string, cancelKind CancelKind) bool {
	if e.Stance != stance {
		return false
	}
	if e.Buttons != input.None && (pressed&e.Buttons) == 0 {
		return false
	}

	switch cancelKind {
	case CancelNone:
		// 中立态：CancelOnly 的招（目标连专属段）不许从中立出
		if e.CancelOnly {
			return false
		}
	case CancelOnHit:
		// 命中取消（后摇通道）：来源招必须在取消来源列表里
		if !slices.Contains(e.CancelFrom, cancelSource) {
			return false
		}
	case CancelFeint:
		// 变招（前摇通道）：来源招必须在变招来源列表里，且必须"变"成不同招
		if !slices.Contains(e.FeintFrom, cancelSource) {
			return false
		}
		if e.MoveID == cancelSource {
			return false
		}
	}
	return true
}
