package combat

import (
	"ftgserver/sim/fixed"
	"ftgserver/sim/input"
)

// MovementState 对齐 C# MovementConfig.cs 的 MovementState : byte（序数 0-9 也对齐 proto）。
type MovementState uint8

const (
	MovementIdle         MovementState = 0
	MovementCrouchEnter  MovementState = 1 // 一次性：下蹲过渡（站→蹲）。中途起身从对称位置接
	MovementCrouch       MovementState = 2 // 循环：蹲姿待机，无位移
	MovementCrouchExit   MovementState = 3 // 一次性：起身过渡（蹲→站）
	MovementWalkForward  MovementState = 4 // 循环：帧号 1..TotalFrames 循环
	MovementWalkBackward MovementState = 5
	MovementDash         MovementState = 6 // 一次性：Startup=起步 / Active=推进 / Recovery=收招
	MovementRun          MovementState = 7 // 循环（需跑步循环 clip）
	MovementBackDash     MovementState = 8 // 一次性，无敌帧写在 MoveData.InvulnFrom/To
	MovementJumping      MovementState = 9 // 一次性：Startup=起跳预备 / Active=腾空 / Recovery=落地
)

// MovementConfig 定义在 definition.go（角色定义的一部分）：全部是招式 Id 引用 + 空中冲刺次数。

// MoveInput 是 MovementController 对座位的窄视图：只读逐帧缓冲 + 消费搓招指令队列。
// 消费方定义接口（Go 惯用法），于是 combat 无需 import seat/motion，*seat.ScriptedSeat
// 靠结构化满足即可注入——解耦的同时对齐 C# 里 MovementController 只吃 IInputSeat 的两个成员。
type MoveInput interface {
	Buffer() *input.Buffer
	Commands() *input.CommandQueue
}

// MovementController 是移动状态机的 Go 移植，对齐 C# MovementController.cs。
//
// 冲刺与跑步是同一条状态线的两个阶段（66 → DashStartup → Dashing → 按住?Running / 松开?Recovery），
// 同一输入无冲突。移动只在可行动（Neutral）时可用——这是"确认帧"成立的前提。
// DashStartup/JumpSquat/DashRecovery 是不可取消的风险窗口，是强力移动的代价。
//
// N2 定点化：位移增量全程 fixed.Vec2，状态机内无浮点；MotionNormalizedTime 是唯一例外——
// 表现层（动画播放头）只读口径，不参与任何模拟决策，headless 服务器不调用。
type MovementController struct {
	config      *MovementConfig
	input       MoveInput
	resolveMove func(string) *MoveData

	state         MovementState
	currentMotion *MoveData
	motionFrame   int

	// 空中冲刺
	airDashesUsed   int
	airDash         *MoveData
	airDashFrame    int
	airDashMirrored bool

	motionMirrored bool // 起始瞬间锁定的朝向，保证 cross-up 不改变已出去的轨迹
	wasUpLastFrame bool // 跳跃边沿检测：按住上不放不应连发跳
}

func NewMovementController(config *MovementConfig, in MoveInput, resolveMove func(string) *MoveData) *MovementController {
	return &MovementController{
		config:      config,
		input:       in,
		resolveMove: resolveMove,
		state:       MovementIdle,
	}
}

// ===================== 对外状态查询 =====================

func (m *MovementController) State() MovementState     { return m.state }
func (m *MovementController) CurrentMotion() *MoveData { return m.currentMotion }
func (m *MovementController) MotionFrame() int         { return m.motionFrame }

func (m *MovementController) IsJumping() bool { return m.state == MovementJumping }

// Phase 复用招式三段语义：跳跃 Startup=起跳预备(地面)/Active=腾空/Recovery=落地；
// 冲刺 Startup=起步(锁死)/Active=推进/Recovery=收招(锁死)。
func (m *MovementController) Phase() MovePhase {
	if m.currentMotion != nil {
		return m.currentMotion.PhaseAt(m.motionFrame)
	}
	return PhaseNone
}

// IsAirDashing 正在空中冲刺（此时跳跃帧被冻结，动画停在当前姿势 → 滞空感）。
func (m *MovementController) IsAirDashing() bool { return m.airDash != nil }

// IsAirborne 是否腾空。起跳预备段仍在地面（可被确反的原因），不算 Airborne。
func (m *MovementController) IsAirborne() bool {
	return m.IsJumping() && (m.Phase() == PhaseActive || m.IsAirDashing())
}

// IsInvulnerable 无敌帧，来自移动招式自身的 MoveData.InvulnFrom/To（后跃步无敌写在那里）。
func (m *MovementController) IsInvulnerable() bool {
	return m.currentMotion != nil && m.currentMotion.InvulnTo > 0 &&
		m.motionFrame >= m.currentMotion.InvulnFrom &&
		m.motionFrame <= m.currentMotion.InvulnTo
}

// CanAct 能否出招。锁死的帧：冲刺起步/收招、起跳预备、落地硬直、后跃全程。
func (m *MovementController) CanAct() bool {
	switch m.state {
	case MovementIdle, MovementCrouchEnter, MovementCrouch, MovementCrouchExit,
		MovementWalkForward, MovementWalkBackward, MovementRun:
		return true
	case MovementDash:
		return m.Phase() == PhaseActive // 冲刺推进段可取消出招（dash cancel）
	case MovementJumping:
		return m.Phase() == PhaseActive || m.IsAirDashing() // 空中招
	default:
		return false // BackDash 全程不可出招
	}
}

// MotionClipID 供表现层播动画：当前移动招式的 clip 名（= MoveID = Animator State）。
func (m *MovementController) MotionClipID() string {
	if m.IsAirDashing() {
		return m.airDash.MoveID
	}
	if m.currentMotion != nil {
		return m.currentMotion.MoveID
	}
	return ""
}

// MotionNormalizedTime 动画归一化播放位置。【表现层专用】唯一保留 float 的口径——
// 只喂动画播放头，不回流模拟；headless 模拟不调用，列此仅为与客户端源逐一对应。
func (m *MovementController) MotionNormalizedTime() float64 {
	if m.IsAirDashing() {
		if m.airDash.TotalFrames() <= 0 {
			return 0
		}
		return clamp01(float64(m.airDashFrame) / float64(m.airDash.TotalFrames()))
	}
	if m.currentMotion == nil || m.currentMotion.TotalFrames() <= 0 {
		return 0
	}
	return clamp01(float64(m.motionFrame) / float64(m.currentMotion.TotalFrames()))
}

// IdleMove 待机招式（判定框永远有数据源）。
func (m *MovementController) IdleMove() *MoveData { return m.resolveMove(m.config.IdleID) }

// ===================== 每帧推进 =====================

// Tick 推进一帧。actionable=false（出招/硬直中）→ 移动归零，但跳跃除外
// （空中被打仍在空中，帧序列继续走完）。位移写回 position。
func (m *MovementController) Tick(actionable, facingRight bool, position *fixed.Vec2) {
	dir := m.input.Buffer().Latest().Direction
	if !facingRight {
		dir = input.Mirror(dir)
	}

	if !actionable && !m.IsJumping() {
		m.Reset()
		m.wasUpLastFrame = isUp(dir)
		return
	}

	*position = position.Add(m.advance(dir, facingRight))
	m.wasUpLastFrame = isUp(dir)
}

func (m *MovementController) advance(dir uint8, facingRight bool) fixed.Vec2 {
	switch m.state {
	case MovementIdle:
		m.tickIdleFrame()
		return m.grounded(dir, facingRight)
	case MovementCrouchEnter:
		return m.crouchingEnter(dir, facingRight)
	case MovementCrouch:
		return m.crouching(dir, facingRight)
	case MovementCrouchExit:
		return m.crouchingExit(dir, facingRight)
	case MovementWalkForward, MovementWalkBackward, MovementRun:
		return m.looping(dir, facingRight)
	case MovementDash, MovementBackDash:
		return m.oneShot(dir, facingRight)
	case MovementJumping:
		return m.jumping(dir, facingRight)
	}
	return fixed.Vec2Zero
}

// grounded 地面中立态：检测冲刺/跳跃/下蹲/行走的起始。
func (m *MovementController) grounded(dir uint8, facingRight bool) fixed.Vec2 {
	// ---- 冲刺 / 后跃（66 / 44）----
	if cmd, ok := m.input.Commands().TryPeek(isDashCmd); ok {
		m.input.Commands().TryConsume(func(c *input.DetectedCommand) bool { return c.ID == cmd.ID })
		forward := cmd.ID == "DASH_F"
		state := MovementDash
		id := m.config.DashID
		if !forward {
			state = MovementBackDash
			id = m.config.BackDashID
		}
		if m.startMotion(id, state, facingRight) {
			return m.currentFrameMotion()
		}
	}

	// ---- 跳跃：要求本帧刚进入上方向（持续按住不连发）----
	if isUp(dir) && !m.wasUpLastFrame {
		id := m.config.JumpNeutralID
		switch dir {
		case 9:
			id = m.config.JumpForwardID
		case 7:
			id = m.config.JumpBackwardID
		}
		if m.startMotion(id, MovementJumping, facingRight) {
			return m.currentFrameMotion()
		}
	}

	// ---- 下蹲：必须先于行走（斜下 1/3 也是蹲，不先拦会误入走路）。蹲姿无位移 ----
	if isDown(dir) && m.startCrouch(facingRight) {
		return fixed.Vec2Zero
	}

	// ---- 行走 ----
	if isForward(dir) && m.startMotion(m.config.WalkForwardID, MovementWalkForward, facingRight) {
		return m.currentFrameMotion()
	}
	if isBackward(dir) && m.startMotion(m.config.WalkBackwardID, MovementWalkBackward, facingRight) {
		return m.currentFrameMotion()
	}

	// 无移动输入 → 保持待机（Idle 是招式，判定框永远有数据源）
	m.ensureIdle(facingRight)
	return fixed.Vec2Zero
}

// startCrouch 进蹲入口：有过渡 clip 则先播过渡，缺数据则直接进循环（可降级）。
func (m *MovementController) startCrouch(facingRight bool) bool {
	if m.config.CrouchEnterID != "" &&
		m.resolveMove(m.config.CrouchEnterID) != nil &&
		m.startMotion(m.config.CrouchEnterID, MovementCrouchEnter, facingRight) {
		return true
	}
	return m.startMotion(m.config.CrouchID, MovementCrouch, facingRight)
}

// crouchingEnter 下蹲过渡（站→蹲）：零位移、可出招。跳跃优先（按上放弃过渡回 grounded）；
// 中途松开下 → 起身从对称位置接续。
func (m *MovementController) crouchingEnter(dir uint8, facingRight bool) fixed.Vec2 {
	if isUp(dir) {
		m.stop()
		return m.grounded(dir, facingRight)
	}

	if !isDown(dir) {
		exit := m.resolveMove(m.config.CrouchExitID)
		if exit == nil {
			m.stop()
			return m.grounded(dir, facingRight)
		}
		frame := mirrorProgress(m.currentMotion, m.motionFrame, exit)
		m.startMotion(m.config.CrouchExitID, MovementCrouchExit, facingRight)
		m.motionFrame = frame
		return fixed.Vec2Zero
	}

	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.startMotion(m.config.CrouchID, MovementCrouch, facingRight) // 蹲到底 → 进循环
	}
	return fixed.Vec2Zero
}

// crouching 蹲姿循环：零位移。松开下 → 起身过渡；按上 → 立刻起跳（演出让路）。
func (m *MovementController) crouching(dir uint8, facingRight bool) fixed.Vec2 {
	if !isDown(dir) {
		if !isUp(dir) {
			if m.resolveMove(m.config.CrouchExitID) != nil {
				m.startMotion(m.config.CrouchExitID, MovementCrouchExit, facingRight)
				return fixed.Vec2Zero
			}
		}
		// 按上（跳跃优先）或没配起身过渡 → 直接回 grounded 重新判定
		m.stop()
		return m.grounded(dir, facingRight)
	}

	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.motionFrame = 1 // 循环
	}
	return fixed.Vec2Zero // 蹲姿不吃 RootMotion
}

// crouchingExit 起身过渡（蹲→站）：播完回 grounded。中途再按下从对称位置接下蹲；按上立刻起跳。
func (m *MovementController) crouchingExit(dir uint8, facingRight bool) fixed.Vec2 {
	if isUp(dir) {
		m.stop()
		return m.grounded(dir, facingRight)
	}

	if isDown(dir) {
		enter := m.resolveMove(m.config.CrouchEnterID)
		if enter != nil {
			frame := mirrorProgress(m.currentMotion, m.motionFrame, enter)
			m.startMotion(m.config.CrouchEnterID, MovementCrouchEnter, facingRight)
			m.motionFrame = frame
		} else {
			m.startMotion(m.config.CrouchID, MovementCrouch, facingRight)
		}
		return fixed.Vec2Zero
	}

	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.stop()
		return m.grounded(dir, facingRight) // 起身完成，当帧恢复地面判定
	}
	return fixed.Vec2Zero
}

// mirrorProgress 把当前过渡进度镜像到反向过渡：进度 p 处改主意 → 反向动画从 (1-p) 处接续。
// 整数域四舍五入（整数乘除 + 加半），替代原 Mathf.Lerp 浮点路径——它决定后续帧号属模拟状态。
func mirrorProgress(from *MoveData, frame int, to *MoveData) int {
	if from == nil || to == nil || from.TotalFrames() <= 0 || to.TotalFrames() <= 0 {
		return 1
	}
	clamped := frame
	if clamped < 0 {
		clamped = 0
	} else if clamped > from.TotalFrames() {
		clamped = from.TotalFrames()
	}
	mirrored := to.TotalFrames() -
		(clamped*to.TotalFrames()+from.TotalFrames()/2)/from.TotalFrames()
	if mirrored < 1 {
		return 1
	}
	if mirrored > to.TotalFrames() {
		return to.TotalFrames()
	}
	return mirrored
}

func (m *MovementController) tickIdleFrame() {
	if m.currentMotion == nil {
		return
	}
	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.motionFrame = 1
	}
}

func (m *MovementController) ensureIdle(facingRight bool) {
	if m.state == MovementIdle && m.currentMotion != nil {
		return
	}
	m.startMotion(m.config.IdleID, MovementIdle, facingRight)
}

// looping 循环型移动（走/跑）：帧号 1..TotalFrames 循环，随时可中断。位移逐帧取自 RootMotion。
func (m *MovementController) looping(dir uint8, facingRight bool) fixed.Vec2 {
	// 按下（含斜下）退出转蹲；按上（含斜上 7/9）也必须退出，否则走路会"吃掉"跳跃。
	var keep bool
	if !isDown(dir) && !isUp(dir) {
		switch m.state {
		case MovementWalkForward:
			keep = isForward(dir)
		case MovementWalkBackward:
			keep = isBackward(dir)
		default: // Run
			keep = isForward(dir)
		}
	}

	if !keep {
		m.stop()
		return m.grounded(dir, facingRight) // 同帧重新判定（转向或起跳）
	}

	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.motionFrame = 1 // 循环
	}
	return m.currentFrameMotion()
}

// oneShot 一次性移动（冲刺/后跃）：帧号走到头即结束。冲刺末尾仍按前且配了跑步 → 转 Run。
func (m *MovementController) oneShot(dir uint8, facingRight bool) fixed.Vec2 {
	m.motionFrame++

	if m.motionFrame > m.currentMotion.TotalFrames() {
		m.stop()
		return m.grounded(dir, facingRight) // 收招当帧即可再行动
	}

	if m.state == MovementDash &&
		m.motionFrame > m.currentMotion.TotalFrames()-m.currentMotion.Recovery &&
		isForward(dir) &&
		m.config.RunID != "" &&
		m.startMotion(m.config.RunID, MovementRun, facingRight) {
		return m.currentFrameMotion()
	}

	return m.currentFrameMotion()
}

// jumping 跳跃推进。位移全来自跳跃招式的 RootMotion（抛物线烘在动画里），
// 无 velocity/gravity——落地就是帧数走完，不需要落地检测。
func (m *MovementController) jumping(dir uint8, facingRight bool) fixed.Vec2 {
	// 起跳预备期（仍在地面）：跳向可修正（SF 式宽容）。离地（Active）后轨迹锁定。
	if !m.IsAirDashing() && m.Phase() == PhaseStartup {
		m.retargetJump(dir)
	}

	// 空中冲刺进行中：冻结跳跃帧 → 动画停在当前姿势（滞空感免费）。
	if m.IsAirDashing() {
		m.airDashFrame++
		if m.airDashFrame > m.airDash.TotalFrames() {
			m.airDash = nil
			m.airDashFrame = 0
			return fixed.Vec2Zero // 本帧交还给跳跃，下一帧继续抛物线
		}
		ad := motionAt(m.airDash, m.airDashFrame)
		if m.airDashMirrored {
			return ad.MirrorX()
		}
		return ad
	}

	// 触发空中冲刺
	if m.Phase() == PhaseActive &&
		m.airDashesUsed < m.config.AirDashCount &&
		m.config.AirDashID != "" {
		if cmd, ok := m.input.Commands().TryPeek(isDashCmd); ok {
			if ad := m.resolveMove(m.config.AirDashID); ad != nil {
				m.input.Commands().TryConsume(func(c *input.DetectedCommand) bool { return c.ID == cmd.ID })
				m.airDashesUsed++
				m.airDash = ad
				m.airDashFrame = 1

				// 后冲取反；朝向在起始瞬间锁定（cross-up 不改变已出去的轨迹）
				backward := cmd.ID == "DASH_B"
				if facingRight {
					m.airDashMirrored = backward
				} else {
					m.airDashMirrored = !backward
				}

				d := motionAt(m.airDash, 1)
				if m.airDashMirrored {
					return d.MirrorX()
				}
				return d
			}
		}
	}

	// 正常推进：吃 RootMotion 的一帧
	m.motionFrame++
	if m.motionFrame > m.currentMotion.TotalFrames() {
		// 帧数走完 = 落地。动画抛物线自然回到地面，无需 Y 钳制
		m.stop()
		m.airDashesUsed = 0
		return fixed.Vec2Zero
	}
	return m.currentFrameMotion()
}

// ===================== 辅助 =====================

// retargetJump 起跳预备期内按当前方向重定跳向。dir 已归一化；motionMirrored 维持起跳瞬间锁定不变。
func (m *MovementController) retargetJump(dir uint8) {
	id := m.config.JumpNeutralID
	if isForward(dir) {
		id = m.config.JumpForwardID
	} else if isBackward(dir) {
		id = m.config.JumpBackwardID
	}
	if m.currentMotion != nil && m.currentMotion.MoveID == id {
		return
	}
	move := m.resolveMove(id)
	if move == nil {
		return // 缺数据时保持原跳向，不炸
	}
	m.currentMotion = move // MotionFrame 保留：三跳预备帧数一致，帧号直接续
}

// startMotion 装载移动招式。缺数据返回 false 由调用方降级（C# 侧此处 Debug.LogError，headless 无日志）。
func (m *MovementController) startMotion(moveID string, state MovementState, facingRight bool) bool {
	if moveID == "" {
		return false
	}
	move := m.resolveMove(moveID)
	if move == nil {
		return false
	}
	m.currentMotion = move
	m.motionFrame = 1
	m.state = state
	m.motionMirrored = !facingRight // 起始瞬间锁定朝向
	return true
}

// stop 结束当前移动招式回到待机（此后不持有招式，由下一次 ensureIdle 补 Idle）。
func (m *MovementController) stop() {
	m.currentMotion = nil
	m.motionFrame = 0
	m.state = MovementIdle
}

// currentFrameMotion 当前移动招式本帧的位移（已转世界空间）。
func (m *MovementController) currentFrameMotion() fixed.Vec2 {
	if m.currentMotion == nil {
		return fixed.Vec2Zero
	}
	mv := motionAt(m.currentMotion, m.motionFrame)
	if m.motionMirrored {
		return mv.MirrorX()
	}
	return mv
}

// motionAt 招式某帧的位移增量（面朝右空间）。越界返回零。
func motionAt(move *MoveData, frame int) fixed.Vec2 {
	rm := move.RootMotion
	if rm == nil {
		return fixed.Vec2Zero
	}
	i := frame - 1
	if i >= 0 && i < len(rm) {
		return rm[i]
	}
	return fixed.Vec2Zero
}

func isDashCmd(c *input.DetectedCommand) bool { return c.ID == "DASH_F" || c.ID == "DASH_B" }

func isUp(dir uint8) bool       { return dir == 7 || dir == 8 || dir == 9 }
func isDown(dir uint8) bool     { return dir == 1 || dir == 2 || dir == 3 }
func isForward(dir uint8) bool  { return dir == 6 || dir == 3 || dir == 9 }
func isBackward(dir uint8) bool { return dir == 4 || dir == 1 || dir == 7 }

func clamp01(v float64) float64 {
	if v < 0 {
		return 0
	}
	if v > 1 {
		return 1
	}
	return v
}

// Reset 受击/出招打断移动。跳跃由调用方保护（空中被打仍在空中）。
// 蹲姿家族【保留】而非清空："我在蹲"的记忆要活过打断——否则蹲攻击收招那帧会从头重播站→蹲过渡闪一帧。
func (m *MovementController) Reset() {
	m.airDash = nil
	m.airDashFrame = 0
	m.airDashesUsed = 0

	if m.state == MovementCrouch ||
		m.state == MovementCrouchEnter ||
		m.state == MovementCrouchExit {
		return
	}

	m.stop()
	// 注意：不清 wasUpLastFrame——清成 false 则手还按着上时恢复当帧会被判"刚按下"→ 意外起跳。
}

// HardReset 回合级全清（与 Reset 语义相反）：新回合一切归零。仅回合系统调用，战斗内打断请用 Reset。
func (m *MovementController) HardReset() {
	m.airDash = nil
	m.airDashFrame = 0
	m.airDashesUsed = 0
	m.motionMirrored = false
	m.wasUpLastFrame = false
	m.stop()
}
