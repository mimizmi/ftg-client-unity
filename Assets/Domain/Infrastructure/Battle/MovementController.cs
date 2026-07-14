using System;
using Domain.Infrastructure.Input;
using Domain.Service;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 移动状态机（纯 C#，帧确定性）。与招式状态机并列，由 FighterState 调度。
    ///
    /// 【冲刺与跑步的统一】两者都是 66 输入，不能是两个独立机制——同一个输入
    /// 不能既是"固定距离突进"又是"持续奔跑"。解法是把它们做成同一条状态线的两个阶段：
    ///   66 → DashStartup → Dashing → [仍按住前?] → Running（循环） → [松开] → DashRecovery
    ///                                → [未按住]   → DashRecovery
    /// 于是"冲出去之后想继续跑就按着"，输入无冲突，手感也自然。
    ///
    /// 【关键约束】移动只在角色可行动（Neutral）时可用。招式中、硬直中不能移动——
    /// 这个约束正是"确认帧"(frame trap)等高级技术成立的前提。
    ///
    /// 【风险窗口的设计】DashStartup / JumpSquat / DashRecovery 都是不可取消的帧，
    /// 它们是这些强力移动的代价：跳跃起跳前 4 帧被抓到就是确反，
    /// 冲刺收招 4 帧内被打就是 Counter Hit。没有这些窗口，移动就是无风险的，游戏会失衡。
    /// </summary>
    public sealed class MovementController
    {
        private readonly MovementConfig config;
        private readonly FightingInputController input;
        private readonly Func<string, MoveData> resolveMove;

        public MovementState State { get; private set; } = MovementState.Idle;
        
        public MoveData CurrentMotion { get; private set; }
        
        public int MotionFrame { get; private set; }

        // 空中冲刺
        private int airDashesUsed;
        private MoveData airDash;
        private int airDashFrame;
        private bool airDashMirrored;
 
        private bool motionMirrored;  // 起始瞬间锁定的朝向，保证 cross-up 不改变已出去的轨迹
        private bool wasUpLastFrame;  // 跳跃边沿检测：按住上不放不应连发跳

        public MovementController(MovementConfig config, FightingInputController input, Func<string, MoveData> resolveMove)
        {
            this.config = config;
            this.input = input;
            this.resolveMove = resolveMove;
        }

        // ===================== 对外状态查询 =====================
 
        public bool IsJumping => State == MovementState.Jumping;
 
        /// <summary>
        /// 当前移动招式的阶段。复用招式的三段语义：
        ///   跳跃：Startup=起跳预备(地面) / Active=腾空 / Recovery=落地硬直
        ///   冲刺：Startup=起步(锁死)   / Active=推进   / Recovery=收招(锁死)
        /// </summary>
        public MovePhase Phase => CurrentMotion != null
            ? CurrentMotion.PhaseAt(MotionFrame)
            : MovePhase.None;
 
        /// <summary>正在空中冲刺（此时跳跃帧被冻结，动画停在当前姿势 → 滞空感）。</summary>
        public bool IsAirDashing => airDash != null;
 
        /// <summary>
        /// 是否腾空。起跳预备段【仍在地面】——这正是跳跃可被确反的原因，不算 Airborne。
        /// </summary>
        public bool IsAirborne => IsJumping && (Phase == MovePhase.Active || IsAirDashing);
 
        /// <summary>无敌帧。来自移动招式自身的 MoveData.InvulnFrom/To（后跃步的无敌就写在那里）。</summary>
        public bool IsInvulnerable =>
            CurrentMotion != null && CurrentMotion.InvulnTo > 0
                                  && MotionFrame >= CurrentMotion.InvulnFrom
                                  && MotionFrame <= CurrentMotion.InvulnTo;

         /// <summary>
        /// 能否出招。锁死的帧：冲刺起步/收招、起跳预备、落地硬直。
        /// 这些不可取消的帧就是强力移动的代价——没有它们，移动就是无风险的。
        /// </summary>
        public bool CanAct
        {
            get
            {
                switch (State)
                {
                    case MovementState.Idle:
                    case MovementState.WalkForward:
                    case MovementState.WalkBackward:
                    case MovementState.Run:
                        return true;
                    case MovementState.Dash:
                        return Phase == MovePhase.Active;  // 冲刺推进段可取消出招（dash cancel）
                    case MovementState.Jumping:
                        return Phase == MovePhase.Active || IsAirDashing;  // 空中招
                    default:
                        return false;  // BackDash 全程不可出招
                }
            }
        }
 
        /// <summary>防御只在地面且非移动锁定帧时成立（空中不可防，街霸式设定）。</summary>
        public bool CanGuard =>
            State == MovementState.Idle
            || State == MovementState.WalkForward
            || State == MovementState.WalkBackward;
 
        /// <summary>供 FighterView 播动画：当前移动招式的 clip 名（= MoveId = Animator State）。</summary>
        public string MotionClipId => IsAirDashing ? airDash.MoveId : CurrentMotion?.MoveId;
 
        /// <summary>
        /// 动画归一化播放位置。一次性动画从头播到尾；循环动画同理（Animator 侧勾 Loop Time）。
        /// </summary>
        public float MotionNormalizedTime
        {
            get
            {
                if (IsAirDashing)
                    return airDash.TotalFrames <= 0 ? 0f
                        : Mathf.Clamp01((airDashFrame - 1f) / airDash.TotalFrames);
 
                if (CurrentMotion == null || CurrentMotion.TotalFrames <= 0) return 0f;
                return Mathf.Clamp01((MotionFrame - 1f) / CurrentMotion.TotalFrames);
            }
        }
 
        // ===================== 每帧推进 =====================
 
        public void Tick(bool actionable, bool facingRight, ref Vector2 position)
        {
            byte dir = facingRight
                ? input.Buffer.Latest.Direction
                : Numpad.Mirror(input.Buffer.Latest.Direction);
 
            // 不可行动（出招中/硬直中）→ 移动归零。跳跃除外：
            // 空中被打仍在空中，跳跃的帧序列继续走完（这也让空中招期间抛物线继续）
            if (!actionable && !IsJumping)
            {
                Reset();
                wasUpLastFrame = IsUp(dir);
                return;
            }
 
            position += Advance(dir, facingRight);
            wasUpLastFrame = IsUp(dir);
        }
 
        private Vector2 Advance(byte dir, bool facingRight)
        {
            switch (State)
            {
                case MovementState.Idle:
                    TickIdleFrame();
                    return Grounded(dir, facingRight);
 
                case MovementState.WalkForward:
                case MovementState.WalkBackward:
                case MovementState.Run:
                    return Looping(dir, facingRight);
 
                case MovementState.Dash:
                case MovementState.BackDash:
                    return OneShot(dir, facingRight);
 
                case MovementState.Jumping:
                    return Jumping(dir, facingRight);
            }
            return Vector2.zero;
        }
 
        /// <summary>地面中立态：检测冲刺/跳跃/行走的起始。</summary>
        private Vector2 Grounded(byte dir, bool facingRight)
        {
            // ---- 冲刺 / 后跃（66 / 44，来自 MotionDetector）----
            if (input.Commands.TryPeek(out DetectedCommand cmd,
                    c => c.Id == "DASH_F" || c.Id == "DASH_B"))
            {
                input.Commands.TryConsume(out _, c => c.Id == cmd.Id);
                bool forward = cmd.Id == "DASH_F";
                if (StartMotion(forward ? config.DashId : config.BackDashId,
                        forward ? MovementState.Dash : MovementState.BackDash, facingRight))
                {
                    return CurrentFrameMotion();
                }
            }
 
            // ---- 跳跃：要求【本帧刚进入】上方向。持续按住不会连发跳 ----
            if (IsUp(dir) && !wasUpLastFrame)
            {
                string id = dir == 9 ? config.JumpForwardId
                    : dir == 7 ? config.JumpBackwardId
                    : config.JumpNeutralId;
                if (StartMotion(id, MovementState.Jumping, facingRight))
                    return CurrentFrameMotion();
            }
 
            // ---- 行走 ----
            if (IsForward(dir) && StartMotion(config.WalkForwardId, MovementState.WalkForward, facingRight))
                return CurrentFrameMotion();
 
            if (IsBackward(dir) && StartMotion(config.WalkBackwardId, MovementState.WalkBackward, facingRight))
                return CurrentFrameMotion();
 
            // 无移动输入 → 保持待机。Idle 是招式，所以判定框永远有数据源
            EnsureIdle(facingRight);
            return Vector2.zero;
        }
        
        private void TickIdleFrame()
        {
            if (CurrentMotion == null) return;
            MotionFrame++;
            if (MotionFrame > CurrentMotion.TotalFrames) MotionFrame = 1;
        }
        
        private void EnsureIdle(bool facingRight)
        {
            if (State == MovementState.Idle && CurrentMotion != null) return;
            StartMotion(config.IdleId, MovementState.Idle, facingRight);
        }
        
        public MoveData IdleMove => resolveMove(config.IdleId);
 
        /// <summary>
        /// 循环型移动（走路/跑步）：帧号在 1..TotalFrames 之间循环，随时可中断。
        /// 位移逐帧取自 RootMotion —— 于是角色的移动【完全跟随动画里脚的实际位移】，
        /// 包括一个步态周期内的自然快慢（蹬地快、抬脚慢），脚下打滑从根本上消失。
        /// </summary>
        private Vector2 Looping(byte dir, bool facingRight)
        {
            bool keep = State == MovementState.WalkForward ? IsForward(dir)
                : State == MovementState.WalkBackward ? IsBackward(dir)
                : IsForward(dir); // Run
 
            if (!keep)
            {
                Stop();
                return Grounded(dir, facingRight); // 同帧重新判定（可能是转向或起跳）
            }
 
            MotionFrame++;
            if (MotionFrame > CurrentMotion.TotalFrames) MotionFrame = 1; // 循环
 
            return CurrentFrameMotion();
        }
 
        /// <summary>一次性移动（冲刺/后跃）：帧号走到头即结束。</summary>
        private Vector2 OneShot(byte dir, bool facingRight)
        {
            MotionFrame++;
 
            if (MotionFrame > CurrentMotion.TotalFrames)
            {
                Stop();
                return Grounded(dir, facingRight); // 收招当帧即可再行动
            }
 
            // 冲刺末尾仍按住前且配了跑步循环 → 转入跑步（街霸三代式）
            if (State == MovementState.Dash
                && MotionFrame > CurrentMotion.TotalFrames - CurrentMotion.Recovery
                && IsForward(dir)
                && !string.IsNullOrEmpty(config.RunId)
                && StartMotion(config.RunId, MovementState.Run, facingRight))
            {
                return CurrentFrameMotion();
            }
 
            return CurrentFrameMotion();
        }
 
        /// <summary>
        /// 跳跃推进。位移全部来自跳跃招式的 RootMotion（抛物线烘在动画里），
        /// 没有 velocity 也没有 gravity —— 落地就是"帧数走完"，不需要落地检测。
        /// </summary>
        private Vector2 Jumping(byte dir, bool facingRight)
        {
            // ---- 空中冲刺进行中：冻结跳跃帧 → 动画停在当前姿势（滞空感是免费的）----
            if (IsAirDashing)
            {
                airDashFrame++;
                if (airDashFrame > airDash.TotalFrames)
                {
                    airDash = null;
                    airDashFrame = 0;
                    return Vector2.zero; // 本帧交还给跳跃，下一帧继续抛物线
                }
                Vector2 ad = MotionAt(airDash, airDashFrame);
                return airDashMirrored ? new Vector2(-ad.x, ad.y) : ad;
            }
 
            // ---- 触发空中冲刺 ----
            if (Phase == MovePhase.Active
                && airDashesUsed < config.AirDashCount
                && !string.IsNullOrEmpty(config.AirDashId)
                && input.Commands.TryPeek(out DetectedCommand cmd,
                       c => c.Id == "DASH_F" || c.Id == "DASH_B"))
            {
                MoveData ad = resolveMove(config.AirDashId);
                if (ad != null)
                {
                    input.Commands.TryConsume(out _, c => c.Id == cmd.Id);
                    airDashesUsed++;
                    airDash = ad;
                    airDashFrame = 1;
 
                    // 后冲时把位移取反；朝向在起始瞬间锁定（cross-up 不改变已出去的轨迹）
                    bool backward = cmd.Id == "DASH_B";
                    airDashMirrored = facingRight ? backward : !backward;
 
                    Vector2 d = MotionAt(airDash, 1);
                    return airDashMirrored ? new Vector2(-d.x, d.y) : d;
                }
            }
 
            // ---- 正常推进：吃 RootMotion 的一帧 ----
            MotionFrame++;
 
            if (MotionFrame > CurrentMotion.TotalFrames)
            {
                // 帧数走完 = 落地完成。动画的抛物线自然回到地面，无需 Y 钳制
                Stop();
                airDashesUsed = 0;
                return Vector2.zero;
            }
 
            return CurrentFrameMotion();
        }
 
        // ===================== 辅助 =====================
 
        private bool StartMotion(string moveId, MovementState state, bool facingRight)
        {
            if (string.IsNullOrEmpty(moveId)) return false;
 
            MoveData move = resolveMove(moveId);
            if (move == null)
            {
                Debug.LogError(
                    $"[MovementController] 找不到移动招式 \"{moveId}\"。" +
                    "移动和招式走同一套数据：需要在 FighterDefinition.Moves 里定义一条 MoveData，" +
                    "帧数用 Startup/Active/Recovery，位移用 FG/Batch Root Motion Baker 烘焙。");
                return false;
            }
 
            CurrentMotion = move;
            MotionFrame = 1;
            State = state;
            motionMirrored = !facingRight; // 起始瞬间锁定朝向
            return true;
        }
 
        /// <summary>结束当前移动招式，回到待机。注意仍然持有 Idle 招式（判定框需要它）。</summary>
        private void Stop()
        {
            CurrentMotion = null;
            MotionFrame = 0;
            State = MovementState.Idle;
        }
 
        /// <summary>取当前移动招式本帧的位移（已转为世界空间）。</summary>
        private Vector2 CurrentFrameMotion()
        {
            if (CurrentMotion == null) return Vector2.zero;
            Vector2 m = MotionAt(CurrentMotion, MotionFrame);
            return motionMirrored ? new Vector2(-m.x, m.y) : m;
        }
 
        /// <summary>取招式某帧的位移增量（面朝右空间）。越界返回零。</summary>
        private static Vector2 MotionAt(MoveData move, int frame)
        {
            Vector2[] rm = move.RootMotion;
            if (rm == null) return Vector2.zero;
            int i = frame - 1;
            return (i >= 0 && i < rm.Length) ? rm[i] : Vector2.zero;
        }
 
        private static bool IsUp(byte dir) => dir == 7 || dir == 8 || dir == 9;
        private static bool IsForward(byte dir) => dir == 6 || dir == 3 || dir == 9;
        private static bool IsBackward(byte dir) => dir == 4 || dir == 1 || dir == 7;
 
        /// <summary>受击/出招打断移动。跳跃由调用方保护（空中被打仍在空中）。</summary>
        public void Reset()
        {
            Stop();
            airDash = null;
            airDashFrame = 0;
            airDashesUsed = 0;
            // 注意：不清 wasUpLastFrame。清成 false 的话，玩家手还按着上时，
            // 恢复可行动的当帧会被判为"刚按下" → 意外起跳。必须松手重按才能跳。
        }
    }
}