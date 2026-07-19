package seat

import (
	"ftgserver/sim/input"
	"ftgserver/sim/motion"
)

// ReplaySeat 逐字回放一段录制好的逐帧输入（direction/held/pressed 原样重放），
// 对齐客户端 ReplaySeat 的语义。对拍必须逐字回放而非从 held 重算 pressed——
// pressed 事件锁存含无法从 held 序列推导的单帧点按位（见 combat.proto Input 注释）。
//
// ManualTick 的加工顺序（推缓冲 → 队列过期 → 搓招检测 → 指令入队）与 ScriptedSeat、
// 与客户端实时座位完全一致：三者喂给核心的东西必须无法区分，这是回放确定性的前提。
type ReplaySeat struct {
	buffer       *input.Buffer
	commands     *input.CommandQueue
	detector     *motion.Detector
	facingRight  bool
	currentFrame int
	frames       []input.Frame
}

var _ Seat = (*ReplaySeat)(nil)

// NewReplaySeat 从录制逐帧输入构造回放座位。缓冲容量 120，与 ScriptedSeat 一致。
// frames[i] 是第 i+1 帧要重放的输入（Frame 字段可留 0，本座位按重放次序覆盖为 currentFrame）。
func NewReplaySeat(frames []input.Frame) *ReplaySeat {
	return &ReplaySeat{
		buffer:      input.NewBuffer(120),
		commands:    input.NewCommandQueue(),
		detector:    &motion.Detector{},
		facingRight: true,
		frames:      frames,
	}
}

func (s *ReplaySeat) Buffer() *input.Buffer         { return s.buffer }
func (s *ReplaySeat) Commands() *input.CommandQueue { return s.commands }
func (s *ReplaySeat) Detector() *motion.Detector    { return s.detector }
func (s *ReplaySeat) FacingRight() bool             { return s.facingRight }
func (s *ReplaySeat) SetFacingRight(v bool)         { s.facingRight = v }
func (s *ReplaySeat) CurrentFrame() int             { return s.currentFrame }

func (s *ReplaySeat) ManualTick() {
	s.currentFrame++

	var f input.Frame
	i := s.currentFrame - 1
	if i >= 0 && i < len(s.frames) {
		f = s.frames[i] // 逐字重放
	} else {
		f = input.Frame{Direction: 5} // 录制耗尽 → 中立
	}
	f.Frame = s.currentFrame
	s.buffer.Push(f)

	s.commands.Tick(s.currentFrame)
	for _, p := range s.detector.DetectAll(s.buffer, s.facingRight) {
		s.commands.Enqueue(p.ID, p.Priority, s.currentFrame)
	}
}
