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

        public MovementState State { get; private set; } = MovementState.Idle;
        public Vector2 Velocity { get; private set; }
        public int StateFrame { get; private set; }

        private int airDashesUsed;
        private int jumpDirection; // 起跳时锁定的水平方向：-1 后, 0 垂直, 1 前

        public MovementController(MovementConfig config, FightingInputController input)
        {
            this.config = config;
            this.input = input;
        }

        public bool IsAirborne =>
            State == MovementState.Airborne || State == MovementState.JumpSquat;

        /// <summary>后跃步的无敌帧——这是后跃能"逃"的原因，也是它存在的价值。</summary>
        public bool IsInvulnerable =>
            State == MovementState.BackDash && StateFrame <= config.BackDashInvulnFrames;

        /// <summary>
        /// 能否出招/防御。冲刺起步、起跳预备、落地硬直、冲刺收招都是"锁死"的帧——
        /// 移动的代价就在这里。空中可以出空中招。
        /// </summary>
        public bool CanAct =>
            State == MovementState.Idle
            || State == MovementState.WalkForward
            || State == MovementState.WalkBackward
            || State == MovementState.Dashing      // 冲刺中可取消出招（dash cancel）
            || State == MovementState.Running      // 跑动中可出招（跑动攻击）
            || State == MovementState.Airborne;    // 空中招

        /// <summary>防御只在地面且非移动锁定帧时成立（空中不可防，这是街霸式设定）。</summary>
        public bool CanGuard =>
            State == MovementState.Idle
            || State == MovementState.WalkForward
            || State == MovementState.WalkBackward;

        /// <summary>由 FighterState 每帧调用。返回本帧的位移增量（面朝右空间）。</summary>
        public Vector2 Tick(bool actionable, bool facingRight, ref Vector2 position)
        {
            StateFrame++;

            // 不可行动（出招中/硬直中）→ 移动状态归零，但空中状态要保留（空中被打仍在空中）
            if (!actionable && !IsAirborne)
            {
                if (State != MovementState.Airborne) Reset();
                return Vector2.zero;
            }

            byte dir = facingRight
                ? input.Buffer.Latest.Direction
                : Numpad.Mirror(input.Buffer.Latest.Direction);

            Vector2 delta = Advance(dir, actionable);

            // 应用位移与重力
            position += delta;

            // 落地检测
            if (State == MovementState.Airborne && position.y <= config.GroundY)
            {
                position.y = config.GroundY;
                Transition(MovementState.Landing);
                Velocity = Vector2.zero;
                airDashesUsed = 0;
            }

            return delta;
        }

        private Vector2 Advance(byte dir, bool actionable)
        {
            switch (State)
            {
                case MovementState.Idle:
                case MovementState.WalkForward:
                case MovementState.WalkBackward:
                    return Grounded(dir, actionable);

                case MovementState.DashStartup:
                    // 起步帧：无位移，不可取消——冲刺的风险窗口
                    if (StateFrame >= config.DashStartupFrames)
                        Transition(MovementState.Dashing);
                    return Vector2.zero;

                case MovementState.Dashing:
                    if (StateFrame >= config.DashFrames)
                    {
                        // 冲刺末尾仍按住前 → 转入跑步循环；否则收招
                        Transition(IsForward(dir) ? MovementState.Running : MovementState.DashRecovery);
                    }
                    return new Vector2(config.DashSpeed, 0f);

                case MovementState.Running:
                    // 跑步：持续按住前才继续，松开即收招
                    if (!IsForward(dir))
                    {
                        Transition(MovementState.DashRecovery);
                        return Vector2.zero;
                    }
                    return new Vector2(config.RunSpeed, 0f);

                case MovementState.DashRecovery:
                    if (StateFrame >= config.DashRecoveryFrames) Transition(MovementState.Idle);
                    return Vector2.zero;

                case MovementState.BackDash:
                {
                    if (StateFrame >= config.BackDashFrames)
                    {
                        Transition(MovementState.Idle);
                        return Vector2.zero;
                    }
                    // 后跃按减速曲线分配位移：起步快、末尾慢，手感更像"跃"而非"滑"
                    float t = StateFrame / (float)config.BackDashFrames;
                    float weight = (1f - t) * 2f / config.BackDashFrames;
                    return new Vector2(-config.BackDashDistance * weight, 0f);
                }

                case MovementState.JumpSquat:
                    // 起跳预备：仍在地面、不可取消——跳跃的风险来源，被抓到就是确反
                    if (StateFrame >= config.JumpSquatFrames) Launch();
                    return Vector2.zero;

                case MovementState.Airborne:
                    return Airborne(dir);

                case MovementState.Landing:
                    if (StateFrame >= config.LandingFrames) Transition(MovementState.Idle);
                    return Vector2.zero;
            }
            return Vector2.zero;
        }

        private Vector2 Grounded(byte dir, bool actionable)
        {
            if (!actionable)
            {
                Transition(MovementState.Idle);
                return Vector2.zero;
            }

            // ---- 高级移动指令（来自 MotionDetector 的 66 / 44 检测）----
            if (input.Commands.TryPeek(out DetectedCommand cmd, c => c.Id == "DASH_F" || c.Id == "DASH_B"))
            {
                input.Commands.TryConsume(out _, c => c.Id == cmd.Id);
                Transition(cmd.Id == "DASH_F" ? MovementState.DashStartup : MovementState.BackDash);
                return Vector2.zero;
            }

            // ---- 跳跃：上方向（7/8/9）----
            if (dir == 7 || dir == 8 || dir == 9)
            {
                jumpDirection = dir == 9 ? 1 : dir == 7 ? -1 : 0;
                Transition(MovementState.JumpSquat);
                return Vector2.zero;
            }

            // ---- 行走 ----
            if (IsForward(dir))
            {
                Transition(MovementState.WalkForward, keepFrame: State == MovementState.WalkForward);
                return new Vector2(config.WalkForwardSpeed, 0f);
            }
            if (IsBackward(dir))
            {
                // 注意：按后 = 走路 + 防御姿态。防御成立与否由 CollisionResolver 在
                // 碰撞发生时裁决（GuardCheck），移动层只管走
                Transition(MovementState.WalkBackward, keepFrame: State == MovementState.WalkBackward);
                return new Vector2(-config.WalkBackwardSpeed, 0f);
            }

            Transition(MovementState.Idle, keepFrame: State == MovementState.Idle);
            return Vector2.zero;
        }

        private Vector2 Airborne(byte dir)
        {
            // 空中冲刺：66 / 44 在空中触发，每跳限次
            if (airDashesUsed < config.AirDashCount
                && input.Commands.TryPeek(out DetectedCommand cmd,
                       c => c.Id == "DASH_F" || c.Id == "DASH_B"))
            {
                input.Commands.TryConsume(out _, c => c.Id == cmd.Id);
                airDashesUsed++;
                float dashDir = cmd.Id == "DASH_F" ? 1f : -1f;
                Velocity = new Vector2(config.AirDashSpeed * dashDir,
                    config.AirDashIgnoresGravity ? 0f : Velocity.y);
                airDashFramesLeft = config.AirDashFrames;
            }

            Vector2 v = Velocity;

            if (airDashFramesLeft > 0)
            {
                airDashFramesLeft--;
                // 空中冲刺期间免疫重力 → 滞空感（罪恶装备式）
                if (!config.AirDashIgnoresGravity) v.y -= config.Gravity;
            }
            else
            {
                v.y -= config.Gravity;
            }

            Velocity = v;
            return v;
        }

        private int airDashFramesLeft;

        private void Launch()
        {
            Velocity = new Vector2(
                jumpDirection * config.JumpHorizontalSpeed,
                config.JumpVelocity);
            Transition(MovementState.Airborne);
        }

        private static bool IsForward(byte dir) => dir == 6 || dir == 3 || dir == 9;
        private static bool IsBackward(byte dir) => dir == 4 || dir == 1 || dir == 7;

        private void Transition(MovementState next, bool keepFrame = false)
        {
            if (State == next && keepFrame) return;
            State = next;
            StateFrame = keepFrame ? StateFrame : 0;
        }

        /// <summary>受击/出招打断移动。空中状态由调用方保护（空中被打仍在空中）。</summary>
        public void Reset()
        {
            State = MovementState.Idle;
            StateFrame = 0;
            Velocity = Vector2.zero;
            airDashFramesLeft = 0;
        }

        /// <summary>被打飞时由战斗层调用，强制进入空中。</summary>
        public void ForceAirborne(Vector2 velocity)
        {
            State = MovementState.Airborne;
            StateFrame = 0;
            Velocity = velocity;
        }
    }
}