package combat

import "ftgserver/sim/fixed"

// BattlePhase 战斗宏观阶段，对齐 C# BattleSimulation.cs 的 BattlePhase : byte。冻结阶段也是模拟状态。
type BattlePhase uint8

const (
	PhaseIntro     BattlePhase = 0 // 开场留白，双方冻结、输入照常采样
	PhaseFighting  BattlePhase = 1 // 交战中：完整攻防管线 + 计时
	PhaseRoundOver BattlePhase = 2 // 回合结束定格
	PhaseMatchOver BattlePhase = 3 // 比赛结束终态
)

// BattleConfig 回合规则参数，对齐 C# BattleConfig。默认 = 经典街霸（99 秒、三局两胜）。
type BattleConfig struct {
	RoundFrames     int
	IntroFrames     int
	RoundOverFrames int
	RoundsToWin     int
	MaxHealth       int
}

// NewBattleConfig 返回与 C# 字段初值一致的默认配置。
func NewBattleConfig() *BattleConfig {
	return &BattleConfig{
		RoundFrames:     99 * 60,
		IntroFrames:     0,
		RoundOverFrames: 120,
		RoundsToWin:     2,
		MaxHealth:       1000,
	}
}

// RoundResult 一回合判决，对齐 C# RoundResult。Winner：1/2=玩家，0=平（双 KO/时间到血同，双方各记一胜）。
type RoundResult struct {
	RoundNumber int
	Winner      int
	ByTimeout   bool
}

// BattleSimulation 战斗模拟纯类，对齐 C# BattleSimulation：一场战斗的全部状态与每帧推进。
// 零渲染依赖，EditMode 测试/回滚重模拟/Go 对拍都直接驱动这里。
//
// 帧序不可打乱：朝向 → 输入采样 → （按阶段）状态推进 → 推挡 → 攻防裁决 → 帧末广播。
// 推挡在攻防之前：先把位置解算干净，再用干净位置做判定。
type BattleSimulation struct {
	P1       *FighterState
	P2       *FighterState
	Resolver *CollisionResolver
	Pushbox  *PushboxResolver
	Config   *BattleConfig

	CurrentFrame         int
	Phase                BattlePhase
	RoundNumber          int
	P1Wins               int
	P2Wins               int
	RoundFramesRemaining int
	P1ComboHits          int
	P2ComboHits          int

	// 事件回调（对齐 C# 的 event；headless 可留 nil）。订阅者不得反向改战斗状态。
	OnHit          func(ev *HitEvent)
	OnRoundStarted func(round int)
	OnRoundEnded   func(result RoundResult)
	OnMatchEnded   func(winner int)
	OnTickFinished func(frame int)

	hitEvents  []*HitEvent
	p1Spawn    fixed.Vec2
	p2Spawn    fixed.Vec2
	phaseTimer int
}

// NewBattleSimulation 出生点以传入 fighter 的当前位置为准（回合重置回这里）。
func NewBattleSimulation(p1, p2 *FighterState, resolver *CollisionResolver, config *BattleConfig) *BattleSimulation {
	if config == nil {
		config = NewBattleConfig()
	}
	return &BattleSimulation{
		P1: p1, P2: p2, Resolver: resolver,
		Pushbox:              NewPushboxResolver(),
		Config:               config,
		Phase:                PhaseIntro,
		RoundNumber:          1,
		RoundFramesRemaining: config.RoundFrames,
		p1Spawn:              p1.Position,
		p2Spawn:              p2.Position,
		phaseTimer:           config.IntroFrames,
	}
}

// RoundSecondsRemaining HUD 计时器读数（向上取整）。
func (s *BattleSimulation) RoundSecondsRemaining() int { return (s.RoundFramesRemaining + 59) / 60 }

// Tick 推进一个逻辑帧（60Hz 语义；调用频率由驱动器负责）。
func (s *BattleSimulation) Tick() {
	s.CurrentFrame++

	// ① 朝向同步：位置关系决定朝向，写回角色与输入座位（搓招镜像依赖它）
	p1FacesRight := s.P1.Position.X.Le(s.P2.Position.X)
	s.P1.FacingRight = p1FacesRight
	s.P2.FacingRight = !p1FacesRight
	s.P1.Seat().SetFacingRight(p1FacesRight)
	s.P2.Seat().SetFacingRight(!p1FacesRight)

	// ② 同帧采样双方输入（内部完成搓招检测与指令入队）。冻结阶段也采样：输入流不许断。
	s.P1.Seat().ManualTick()
	s.P2.Seat().ManualTick()

	switch s.Phase {
	case PhaseIntro:
		// IntroFrames=0 时首帧即降为 -1 → 立刻开打
		s.phaseTimer--
		if s.phaseTimer <= 0 {
			s.Phase = PhaseFighting
			if s.OnRoundStarted != nil {
				s.OnRoundStarted(s.RoundNumber)
			}
			s.tickFighting()
		}
	case PhaseFighting:
		s.tickFighting()
	case PhaseRoundOver:
		s.phaseTimer--
		if s.phaseTimer <= 0 {
			s.advanceRound()
		}
	case PhaseMatchOver:
		// 终态：等待外部处置
	}

	// ⑥ 帧末广播
	if s.OnTickFinished != nil {
		s.OnTickFinished(s.CurrentFrame)
	}
}

func (s *BattleSimulation) tickFighting() {
	// ③ 双方状态推进（消费指令、招式帧+1、移动状态机、硬直倒计时）
	s.P1.Tick(s.CurrentFrame)
	s.P2.Tick(s.CurrentFrame)

	// 连段判据采样点：必须在攻防裁决之前记录守方是否已在硬直
	p1WasStunned := s.P1.Status() == StatusHitstun
	p2WasStunned := s.P2.Status() == StatusHitstun

	// ④ 推挡解算
	s.Pushbox.Resolve(s.P1, s.P2)

	// ⑤ 碰撞与攻防裁决（对称检测，支持相杀）
	s.Resolver.Resolve(s.CurrentFrame, s.P1, s.P2, &s.hitEvents)
	hitOnP1, hitOnP2 := false, false
	for _, ev := range s.hitEvents {
		if ev.Outcome == OutcomeHit || ev.Outcome == OutcomeCounterHit {
			if ev.Defender == s.P2 {
				if p2WasStunned {
					s.P1ComboHits++
				} else {
					s.P1ComboHits = 1
				}
				hitOnP2 = true
			} else {
				if p1WasStunned {
					s.P2ComboHits++
				} else {
					s.P2ComboHits = 1
				}
				hitOnP1 = true
			}
		}
		if s.OnHit != nil {
			s.OnHit(ev)
		}
	}

	// 守方回到非硬直且本帧没挨新打 → 连段链断开
	if !hitOnP2 && s.P2.Status() != StatusHitstun {
		s.P1ComboHits = 0
	}
	if !hitOnP1 && s.P1.Status() != StatusHitstun {
		s.P2ComboHits = 0
	}

	s.RoundFramesRemaining--
	s.checkRoundEnd()
}

func (s *BattleSimulation) checkRoundEnd() {
	p1Dead := s.P1.Health <= 0
	p2Dead := s.P2.Health <= 0
	timeout := s.RoundFramesRemaining <= 0
	if !p1Dead && !p2Dead && !timeout {
		return
	}

	var winner int
	if p1Dead || p2Dead {
		switch {
		case p1Dead && p2Dead:
			winner = 0 // 双 KO = 平
		case p1Dead:
			winner = 2
		default:
			winner = 1
		}
	} else {
		switch {
		case s.P1.Health == s.P2.Health:
			winner = 0
		case s.P1.Health > s.P2.Health:
			winner = 1
		default:
			winner = 2
		}
	}

	// 平局双方各记一胜（经典规则）
	if winner != 2 {
		s.P1Wins++
	}
	if winner != 1 {
		s.P2Wins++
	}

	s.Phase = PhaseRoundOver
	s.phaseTimer = s.Config.RoundOverFrames
	if s.OnRoundEnded != nil {
		s.OnRoundEnded(RoundResult{RoundNumber: s.RoundNumber, Winner: winner, ByTimeout: timeout})
	}
}

func (s *BattleSimulation) advanceRound() {
	if s.P1Wins >= s.Config.RoundsToWin || s.P2Wins >= s.Config.RoundsToWin {
		s.Phase = PhaseMatchOver
		var matchWinner int
		switch {
		case s.P1Wins == s.P2Wins:
			matchWinner = 0
		case s.P1Wins > s.P2Wins:
			matchWinner = 1
		default:
			matchWinner = 2
		}
		if s.OnMatchEnded != nil {
			s.OnMatchEnded(matchWinner)
		}
		return
	}

	s.RoundNumber++
	s.P1.ResetForRound(s.p1Spawn, s.Config.MaxHealth)
	s.P2.ResetForRound(s.p2Spawn, s.Config.MaxHealth)
	s.P1ComboHits = 0
	s.P2ComboHits = 0
	s.RoundFramesRemaining = s.Config.RoundFrames
	s.phaseTimer = s.Config.IntroFrames
	s.Phase = PhaseIntro // IntroFrames=0 → 下一帧直接开打
}
