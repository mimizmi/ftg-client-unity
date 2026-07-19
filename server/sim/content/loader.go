// Package content 是帧数据 JSON 的 Go 装载器，对齐客户端
// Assets/Domain/Infrastructure/Battle/BoxDataLoader.cs。
// Go 权威模拟读【同一份】JSON（Assets/BoxData/{角色}_boxes.json 与 _rootmotion.json），
// 这是跨语言对拍成立的前提：两端吃相同的判定框与位移数据。
//
// 边界纪律：坐标在 C# 是 float，这里必须用 float32 解析——同一十进制串解析出同一
// float32，再经 fixed.FromFloat 得同一 Fix.Raw，天然逐位对齐。
package content

import (
	"encoding/json"
	"os"

	"ftgserver/sim/combat"
	"ftgserver/sim/fixed"
)

// ---- 判定框 JSON（对齐 C# CharacterBoxData / MoveBoxData）----

// MoveBoxData 是一个招式的手工创作数据（帧分割、判定框、无敌帧）。
// Tracks 直接复用 combat.BoxTrack —— JSON 反序列化即得运行时轨道对象。
type MoveBoxData struct {
	MoveID      string `json:"MoveId"`
	TotalFrames int
	Startup     int
	Active      int
	Recovery    int
	InvulnFrom  int
	InvulnTo    int
	Tracks      []*combat.BoxTrack
}

// HasFrameSplit 三段之和 > 0 才视为 JSON 里真有帧分割（对齐 C#）。
func (m *MoveBoxData) HasFrameSplit() bool { return m.Startup+m.Active+m.Recovery > 0 }

// CharacterBoxData 是一个角色的全部手工数据（对应 {角色}_boxes.json）。
type CharacterBoxData struct {
	Version     int
	CharacterID string `json:"CharacterId"`
	Moves       []MoveBoxData
}

func (c *CharacterBoxData) Find(moveID string) *MoveBoxData {
	for i := range c.Moves {
		if c.Moves[i].MoveID == moveID {
			return &c.Moves[i]
		}
	}
	return nil
}

// ---- 位移 JSON（对齐 C# CharacterRootMotion / MoveRootMotion）----

type motionSample struct {
	X float32 `json:"x"`
	Y float32 `json:"y"`
}

type MoveRootMotion struct {
	MoveID string `json:"MoveId"`
	Frames int
	Motion []motionSample
}

type CharacterRootMotion struct {
	Version     int
	CharacterID string `json:"CharacterId"`
	ForwardAxis string
	Moves       []MoveRootMotion
}

func (c *CharacterRootMotion) Find(moveID string) *MoveRootMotion {
	for i := range c.Moves {
		if c.Moves[i].MoveID == moveID {
			return &c.Moves[i]
		}
	}
	return nil
}

// ---- 装载 ----

func LoadBoxes(path string) (*CharacterBoxData, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var data CharacterBoxData
	if err := json.Unmarshal(raw, &data); err != nil {
		return nil, err
	}
	return &data, nil
}

func LoadRootMotion(path string) (*CharacterRootMotion, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var data CharacterRootMotion
	if err := json.Unmarshal(raw, &data); err != nil {
		return nil, err
	}
	return &data, nil
}

// ---- 注入到运行时 MoveData（对齐 C# BoxDataLoader.ApplyBoxes / ApplyRootMotion）----

// ApplyBoxes 注入判定框、帧分割、无敌帧。只在 JSON 真有值时覆盖（允许代码占位）。
func ApplyBoxes(boxes *CharacterBoxData, move *combat.MoveData) {
	data := boxes.Find(move.MoveID)
	if data == nil {
		return
	}
	move.BoxTracks = data.Tracks
	if data.HasFrameSplit() {
		move.Startup = data.Startup
		move.Active = data.Active
		move.Recovery = data.Recovery
	}
	if data.InvulnTo > 0 {
		move.InvulnFrom = data.InvulnFrom
		move.InvulnTo = data.InvulnTo
	}
}

// ApplyRootMotion 注入位移（边界转换点：float→定点一次性转换）。
// 取不到 = 原地招式，RootMotion 保持 nil，语义正确无需特判。
func ApplyRootMotion(motion *CharacterRootMotion, move *combat.MoveData) {
	rm := motion.Find(move.MoveID)
	if rm == nil || len(rm.Motion) == 0 {
		return
	}
	fixedMotion := make([]fixed.Vec2, len(rm.Motion))
	for i, s := range rm.Motion {
		fixedMotion[i] = fixed.Vec2FromFloat(s.X, s.Y)
	}
	move.RootMotion = fixedMotion
}

// Apply 加载并注入一个角色的全部动画派生数据到给定招式集。
func Apply(moves []*combat.MoveData, boxes *CharacterBoxData, motion *CharacterRootMotion) {
	for _, move := range moves {
		ApplyBoxes(boxes, move)
		ApplyRootMotion(motion, move)
	}
}
