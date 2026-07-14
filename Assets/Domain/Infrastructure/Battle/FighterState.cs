using System;
using System.Collections.Generic;
using Domain.Infrastructure.Input;
using Domain.Service;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum FighterStatus : byte
    {
        Neutral,
        Attacking,
        CounterStance,
        Hitstun,
        Blockstun
    }
    
    public readonly struct FighterSnapshot
    {
        public readonly int Frame;
        public readonly Vector2 Position;
        public readonly bool FacingRight;
        public readonly FighterStatus Status;
        public readonly string MoveId;
        public readonly int MoveFrame;
        public readonly MovePhase Phase;
 
        public FighterSnapshot(int frame, FighterState f)
        {
            Frame = frame;
            Position = f.Position;
            FacingRight = f.FacingRight;
            Status = f.Status;
            MoveId = f.CurrentMove?.MoveId;
            MoveFrame = f.MoveFrame;
            Phase = f.Phase;
        }
    }
    
    public sealed class FighterState
    {
        public string Name;
        public Vector2 Position;
        public bool FacingRight = true;
        public int Health = 1000;
 
        private readonly FightingInputController input;
        private readonly MoveTable moveTable;
        private readonly Dictionary<string, MoveData> moves = new Dictionary<string, MoveData>();
 
        public FighterStatus Status { get; private set; } = FighterStatus.Neutral;
        public MoveData CurrentMove { get; private set; }
        public int MoveFrame { get; private set; }
 
        private int stunRemaining;
        private bool moveConnected; // 当前招是否已命中过（防止多段重复判定，多段招需扩展）
 
        /// <summary>出招瞬间广播——对手侧"看到对方起手"的反应系统订阅这里，而不是去读按键。</summary>
        public event Action<FighterState, MoveData> MoveStarted;
 
        public FighterState(FightingInputController input, MoveTable moveTable, MovementConfig movementConfig)
        {
            this.input = input;
            this.moveTable = moveTable;
            Movement = new MovementController(movementConfig, input, 
                id => moves.TryGetValue(id , out MoveData m) ? m : null);
        }
        
        public MovementController Movement { get; }
 
        /// <summary>
        /// 当前姿态。招式表用它把同一输入解析成不同招式（5LP / 2LP / j.LP）。
        /// 目前由方向键推断；将来接入跳跃系统后 Airborne 应由 Y 坐标/跳跃状态决定。
        /// </summary>
        public Stance CurrentStance
        {
            get
            {
                if (Movement.IsAirborne) return Stance.Airborne;
                byte dir = FacingRight
                    ? input.Buffer.Latest.Direction
                    : Numpad.Mirror(input.Buffer.Latest.Direction);
                return (dir == 1 || dir == 2 || dir == 3) ? Stance.Crouching : Stance.Standing;
            }
        }
 
        // ---- 对外只读视图 ----
        public InputBuffer InputHistory => input.Buffer;   // 拒止/拆投回看 & 假人读意图
        public FightingInputController InputController => input;
        public CommandQueue Commands => input.Commands;
 
        public MovePhase Phase =>
            (Status == FighterStatus.Attacking || Status == FighterStatus.CounterStance) && CurrentMove != null
                ? CurrentMove.PhaseAt(MoveFrame)
                : MovePhase.None;
 
        public bool Actionable => Status == FighterStatus.Neutral;
 
        /// <summary>
        /// 无敌。两个来源：招式自带的无敌帧（升龙 1~8 帧），
        /// 以及移动状态机的后跃步无敌帧——后者是后跃能"逃"的原因。
        /// </summary>
        public bool IsInvulnerable =>
            (CurrentMove != null && CurrentMove.InvulnTo > 0
                                 && MoveFrame >= CurrentMove.InvulnFrom && MoveFrame <= CurrentMove.InvulnTo
                                 && (Status == FighterStatus.Attacking || Status == FighterStatus.CounterStance))
            || Movement.IsInvulnerable;

        public bool CounterCatchActive =>
            Status == FighterStatus.CounterStance && CurrentMove != null
                                                  && MoveFrame >= CurrentMove.CatchFrom && MoveFrame <= CurrentMove.CatchTo;

        public bool CanMoveConnect =>
            Status == FighterStatus.Attacking && CurrentMove != null
                                              && CurrentMove.HasBoxes(BoxKind.Hit) && !moveConnected;
 
        public void CollectHurtboxes(List<Box> results)
        {
            if (TryCollect(BoxKind.Hurt, results)) return;
            results.Clear();
            
            Debug.LogWarning(
                $"[FighterState] {Name} 本帧没有受击框：当前动作未在 HitboxEditor 里画 Hurt 框，" +
                "且待机招式也没有。角色此刻【打不中】——请补画。", null);
        }
        
        /// <summary>
        /// 收集本帧的推挡框（防重叠）。同样走回退链。
        ///
        /// 推挡框在招式间通常【不变】（就是那根"身体柱子"）——若随招式伸缩，
        /// 角色会被自己的招式推走，位置变得不可预测。所以实践上是：
        /// 在 HitboxEditor 里给待机画一次，其余招式用"从招式复制 Hurt/Push"复制过去，
        /// 只有蹲姿、趴地、大跳这类真正改变体积的动作才单独调。
        /// </summary>
        public void CollectPushboxes(List<Box> results)
        {
            if (TryCollect(BoxKind.Push, results)) return;
            results.Clear(); // 无推挡框 = 不参与推挡（例如某些浮空/倒地状态，这是合法的）
        }

        private bool TryCollect(BoxKind kind, List<Box> results)
        {
            if (CurrentMove != null
                && (Status == FighterStatus.Attacking || Status == FighterStatus.CounterStance)
                && CurrentMove.HasBoxes(kind))
            {
                CurrentMove.CollectBoxes(MoveFrame, kind, results);
                if (results.Count > 0) return true;
            }

            MoveData motion = Movement.CurrentMotion;
            if (motion != null && motion.HasBoxes(kind))
            {
                motion.CollectBoxes(Movement.MotionFrame, kind, results);
                if (results.Count > 0) return true;
            }

            MoveData idle = Movement.IdleMove;
            if (idle != null && idle.HasBoxes(kind))
            {
                idle.CollectBoxes(1, kind, results);
                if (results.Count > 0) return true;
            }

            return false;
        }

        public FighterSnapshot Snapshot(int frame) => new FighterSnapshot(frame, this);
 
        public void AddMove(MoveData move) => moves[move.MoveId] = move;
 
        /// <summary>由 BattleLoop 每逻辑帧调用（输入采样之后、碰撞裁决之前）。</summary>
        public void Tick(int frame)
        {
            TickCombat();

            // 移动只在可行动时生效。出招中/硬直中，移动层自动归零（空中状态除外——
            // 空中被打仍在空中，重力照常作用）
            Movement.Tick(Actionable, FacingRight, ref Position);
        }

        private void TickCombat()
        {
            switch (Status)
            {
                case FighterStatus.Hitstun:
                case FighterStatus.Blockstun:
                    stunRemaining--;
                    if (stunRemaining > 0) return;
                    Status = FighterStatus.Neutral;
                    // 硬直结束的这一帧立即可行动 → 队列里预输入的招当帧出 = reversal 手感
                    goto case FighterStatus.Neutral;

                case FighterStatus.Attacking:
                case FighterStatus.CounterStance:
                    MoveFrame++;
                    if (MoveFrame <= CurrentMove.TotalFrames)
                    {
                        ApplyRootMotion(); // 逻辑位移在这里结算，与判定同帧、同确定性
                        TryCancel();       // 招式进行中：命中后可被取消 → 连招
                        return;
                    }
                    EndMove();
                    goto case FighterStatus.Neutral; // 收招当帧即可行动（首个可行动帧）

                case FighterStatus.Neutral:
                    // 移动状态机锁死的帧（起跳预备、冲刺起步、落地硬直）不能出招——
                    // 这些窗口正是强力移动的代价
                    if (Movement.CanAct) TryAct(null);
                    return;
            }
        }
 
        /// <summary>
        /// 连招取消：当前招【已命中或被防】且处于取消窗口内时，可被新招打断。
        /// 取消窗口 = 从判定帧开始到招式结束（CancelWindowFrom 可按招微调）。
        /// 未命中（放空）不可取消——这是格斗游戏的基本公平性：空挥要吃后摇。
        /// </summary>
        private void TryCancel()
        {
            if (!moveConnected) return; // 没打中，不给取消
            if (CurrentMove.CancelFrom > 0 && MoveFrame < CurrentMove.CancelFrom) return;

            TryAct(CurrentMove.MoveId); // 以当前招为取消来源解析新招
        }
 
        private void TryAct(string cancelSource)
        {
            // ① 搓招指令（队列带优先级与预输入窗口）→ 经招式表解析成具体招式
            if (input.Commands.TryPeek(out DetectedCommand cmd,
                    c => moveTable.ResolveCommand(
                        c.Id, input.Buffer.Latest.Pressed, CurrentStance, cancelSource, this) != null))
            {
                string moveId = moveTable.ResolveCommand(
                    cmd.Id, input.Buffer.Latest.Pressed, CurrentStance, cancelSource, this);
                input.Commands.TryConsume(out _, c => c.Id == cmd.Id); // 确认可出招后才消费
                StartMove(moveId);
                return;
            }
 
            InputFrame latest = input.Buffer.Latest;
 
            // ② 组合键投（LP+LK 同时按下）。投不能取消出招
            if (cancelSource == null)
            {
                bool throwInput =
                    ((latest.Pressed & ButtonMask.LP) != 0 && (latest.Held & ButtonMask.LK) != 0) ||
                    ((latest.Pressed & ButtonMask.LK) != 0 && (latest.Held & ButtonMask.LP) != 0);
                if (throwInput && moves.ContainsKey("THROW"))
                {
                    StartMove("THROW");
                    return;
                }
            }
 
            // ③ 普通技：裸按键 → 招式表解析（姿态决定 5LP / 2LP / j.LP）
            if (latest.Pressed != ButtonMask.None)
            {
                string moveId = moveTable.ResolveButton(
                    latest.Pressed, CurrentStance, cancelSource, this);
                if (moveId != null) StartMove(moveId);
            }
        }
 
        public bool StartMove(string moveId)
        {
            if (!moves.TryGetValue(moveId, out MoveData move)) return false;

            CurrentMove = move;
            MoveFrame = 1;
            moveConnected = false;
            Status = move.IsCounterStance ? FighterStatus.CounterStance : FighterStatus.Attacking;
            MoveStarted?.Invoke(this, move);
            ApplyRootMotion(); // 出招当帧（第 1 帧）的位移
            return true;
        }

        /// <summary>
        /// 消费 MoveData.RootMotion 中当前帧的位移增量。位移定义在"面朝右"空间，
        /// 按当前朝向镜像 X——与搓招、判定框的镜像规则完全一致。
        /// 被打断（受击/被投）时招式直接结束，后续位移自然不再结算，
        /// 不存在 Animator root motion 那种"中断残留"问题。
        /// </summary>
        private void ApplyRootMotion()
        {
            Vector2[] motion = CurrentMove?.RootMotion;
            if (motion == null) return;

            int index = MoveFrame - 1;
            if (index < 0 || index >= motion.Length) return;

            Vector2 delta = motion[index];
            if (!FacingRight) delta.x = -delta.x;
            Position += delta;
        }

        private void EndMove()
        {
            CurrentMove = null;
            MoveFrame = 0;
            moveConnected = false;
            Status = FighterStatus.Neutral;
        }

        /// <summary>
        /// 防御成立检查：按住后方向，且方向档位与攻击位置属性匹配。
        /// Low 必须蹲防（1），Overhead 必须站防（4），Mid 站蹲皆可。
        /// </summary>
        public bool GuardCheck(AttackAttribute attack)
        {
            if (Status != FighterStatus.Neutral && Status != FighterStatus.Blockstun) return false;

            // 移动状态限制：空中、冲刺、起跳预备、落地硬直中都不能防御——
            // 这正是"跳跃有风险"和"冲刺有风险"的来源
            if (!Movement.CanGuard) return false;

            byte dir = FacingRight
                ? input.Buffer.Latest.Direction
                : Numpad.Mirror(input.Buffer.Latest.Direction);

            if ((attack & AttackAttribute.Low) != 0) return dir == 1;
            if ((attack & AttackAttribute.Overhead) != 0) return dir == 4;
            return dir == 4 || dir == 1;
        }

        // ---- 由 CollisionResolver 调用的结果施加 ----

        public void MarkMoveConnected() => moveConnected = true;

        public void ApplyHit(int damage, int hitstunFrames)
        {
            Health -= damage;
            EndMove();
            Status = FighterStatus.Hitstun;
            stunRemaining = hitstunFrames;
            // 受击瞬间清空旧指令：受击前搓好的招作废；硬直期间新搓的会重新入队 → 这正是 reversal
            input.Commands.Clear();

            // 受击打断移动。但空中被打仍在空中——重力继续作用，落地才结束
            if (!Movement.IsAirborne) Movement.Reset();
        }

        public void ApplyBlockstun(int frames)
        {
            EndMove();
            Status = FighterStatus.Blockstun;
            stunRemaining = frames;
        }
    }
}