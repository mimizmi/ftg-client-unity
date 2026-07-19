package seat

import (
	"maps"

	"ftgserver/sim/input"
	"ftgserver/sim/motion"
)

// NetworkSeat 是帧同步/回滚（N5）的座位：它不自采输入，而是消费"已确认"的逐帧输入——
// 本地帧由本端采样后确认，远端帧由网络包到达后确认。核心（FighterState/Movement）
// 完全不知道输入来自设备还是网线，这正是回放/帧同步/回滚共用同一座位契约的意义
// （见 [[ReplaySeat]] 与 combat.MoveInput 窄接口）。
//
// 与 ScriptedSeat/ReplaySeat 逐字一致的加工顺序（推缓冲 → 队列过期 → 搓招检测 → 指令入队）
// 是确定性前提：三种座位喂给核心的东西必须无法区分。
//
// inbox 按【绝对帧号】索引已确认输入。lockstep 调度器保证：只有当双方座位都
// Confirmed(N) 时才 sim.Tick()——所以 ManualTick 落到未确认帧属于调度 bug，
// 此处退化为中立仅作防御，不该在正常帧同步流程里发生。
type NetworkSeat struct {
	buffer       *input.Buffer
	commands     *input.CommandQueue
	detector     *motion.Detector
	facingRight  bool
	currentFrame int
	inbox        map[int]input.Frame
}

var _ Seat = (*NetworkSeat)(nil)

// NewNetworkSeat 缓冲容量 120，与 ScriptedSeat/ReplaySeat 一致。
func NewNetworkSeat() *NetworkSeat {
	return &NetworkSeat{
		buffer:      input.NewBuffer(120),
		commands:    input.NewCommandQueue(),
		detector:    &motion.Detector{},
		facingRight: true,
		inbox:       make(map[int]input.Frame),
	}
}

func (s *NetworkSeat) Buffer() *input.Buffer         { return s.buffer }
func (s *NetworkSeat) Commands() *input.CommandQueue { return s.commands }
func (s *NetworkSeat) Detector() *motion.Detector    { return s.detector }
func (s *NetworkSeat) FacingRight() bool             { return s.facingRight }
func (s *NetworkSeat) SetFacingRight(v bool)         { s.facingRight = v }
func (s *NetworkSeat) CurrentFrame() int             { return s.currentFrame }

// Confirm 确认绝对帧号 frame 的输入（本地采样或远端到达都走这里）。
// 幂等：重复确认同一帧的同一输入无副作用（lockstep 里同一帧只会确认一次）。
func (s *NetworkSeat) Confirm(frame int, f input.Frame) {
	f.Frame = frame
	s.inbox[frame] = f
}

// Confirmed 报告绝对帧号 frame 的输入是否已就位。调度器用它决定能否推进。
func (s *NetworkSeat) Confirmed(frame int) bool {
	_, ok := s.inbox[frame]
	return ok
}

// Clone 深快照座位（回滚存档用）：缓冲/指令队列深拷贝，inbox 复制一份，
// detector 是不可变的搓招模式表（构建后只读）故共享。回滚还原后重新 Confirm+Tick
// 即可从这一帧确定地重放。
func (s *NetworkSeat) Clone() *NetworkSeat {
	ns := &NetworkSeat{
		buffer:       s.buffer.Clone(),
		commands:     s.commands.Clone(),
		detector:     s.detector, // 不可变，共享
		facingRight:  s.facingRight,
		currentFrame: s.currentFrame,
		inbox:        make(map[int]input.Frame, len(s.inbox)),
	}
	maps.Copy(ns.inbox, s.inbox)
	return ns
}

func (s *NetworkSeat) ManualTick() {
	s.currentFrame++

	f, ok := s.inbox[s.currentFrame]
	if !ok {
		f = input.Frame{Frame: s.currentFrame, Direction: 5} // 防御：不该发生（见类型注释）
	}
	s.buffer.Push(f)
	delete(s.inbox, s.currentFrame) // 已消费，回滚重放时会重新 Confirm

	s.commands.Tick(s.currentFrame)
	for _, p := range s.detector.DetectAll(s.buffer, s.facingRight) {
		s.commands.Enqueue(p.ID, p.Priority, s.currentFrame)
	}
}
