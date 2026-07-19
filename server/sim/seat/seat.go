// Package seat 是"输入座位"的 Go 移植，对齐客户端
// Assets/Domain/Infrastructure/Input/IInputSeat.cs + Tests/EditMode/ScriptedSeat.cs。
//
// 模拟核心（MovementController/FighterState）只透过座位读输入，不知道输入从哪来：
// 脚本序列、录像回放、将来的网络远端帧都可注入——这是回滚网络"输入即状态"纪律的接口化落点。
package seat

import (
	"ftgserver/sim/input"
	"ftgserver/sim/motion"
)

// Seat 是一个座位的完整输入源。核心只需要 Buffer+Commands（见 combat.MoveInput 窄接口），
// Detector/ManualTick 是座位自身把原始输入加工成缓冲+指令队列的内部管线。
type Seat interface {
	Buffer() *input.Buffer
	Commands() *input.CommandQueue
	Detector() *motion.Detector
	FacingRight() bool
	SetFacingRight(v bool)
	ManualTick()
}

// ScriptedInput 是一帧脚本输入：方向（世界系 Numpad）+ 按住的键。边沿由座位推导。
type ScriptedInput struct {
	Direction uint8
	Held      input.ButtonMask
}

// ScriptedSeat 用纯函数 (帧号 → 输入) 替代真实设备驱动完整模拟，对齐 C# ScriptedSeat。
// ManualTick 的处理顺序（采样 → 队列过期 → 搓招检测 → 指令入队）与客户端
// FightingInputController.GamePlayLogicTick 一致——两者喂给核心的东西必须无法区分。
// 这也是回放/假人座位的原型。
type ScriptedSeat struct {
	buffer       *input.Buffer
	commands     *input.CommandQueue
	detector     *motion.Detector
	facingRight  bool
	currentFrame int
	script       func(frame int) ScriptedInput
	prevHeld     input.ButtonMask
}

var _ Seat = (*ScriptedSeat)(nil)

// NewScriptedSeat 缓冲容量 120 帧，对齐 C# ScriptedSeat 的 new InputBuffer(120)。
func NewScriptedSeat(script func(frame int) ScriptedInput) *ScriptedSeat {
	return &ScriptedSeat{
		buffer:      input.NewBuffer(120),
		commands:    input.NewCommandQueue(),
		detector:    &motion.Detector{},
		facingRight: true,
		script:      script,
	}
}

// 回放座位见 replay.go 的 ReplaySeat（逐字回放录制的 direction/held/pressed，
// 对拍必须保真 pressed——含无法从 held 推导的单帧点按位）。

func (s *ScriptedSeat) Buffer() *input.Buffer         { return s.buffer }
func (s *ScriptedSeat) Commands() *input.CommandQueue { return s.commands }
func (s *ScriptedSeat) Detector() *motion.Detector    { return s.detector }
func (s *ScriptedSeat) FacingRight() bool             { return s.facingRight }
func (s *ScriptedSeat) SetFacingRight(v bool)         { s.facingRight = v }
func (s *ScriptedSeat) CurrentFrame() int             { return s.currentFrame }

func (s *ScriptedSeat) ManualTick() {
	s.currentFrame++
	in := s.script(s.currentFrame)

	held := in.Held
	s.buffer.Push(input.Frame{
		Frame:     s.currentFrame,
		Direction: in.Direction,
		Held:      held,
		Pressed:   held &^ s.prevHeld,
		Released:  s.prevHeld &^ held,
	})
	s.prevHeld = held

	s.commands.Tick(s.currentFrame)
	for _, p := range s.detector.DetectAll(s.buffer, s.facingRight) {
		s.commands.Enqueue(p.ID, p.Priority, s.currentFrame)
	}
}
