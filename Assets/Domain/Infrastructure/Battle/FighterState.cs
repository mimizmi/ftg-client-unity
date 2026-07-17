using System;
using System.Collections.Generic;
using Domain.Infrastructure.Input;
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
 
        private readonly IInputSeat input;
        private readonly MoveTable moveTable;
        private readonly Dictionary<string, MoveData> moves = new Dictionary<string, MoveData>();
 
        public FighterStatus Status { get; private set; } = FighterStatus.Neutral;
        public MoveData CurrentMove { get; private set; }
        public int MoveFrame { get; private set; }

        /// <summary>当前受击类别（表现层据此选受击 clip）。仅在 Hitstun 期间有意义。</summary>
        public HitReaction CurrentReaction { get; private set; }

        /// <summary>是否处于顿帧（命中定格）中。</summary>
        public bool InHitstop => hitstop > 0;

        /// <summary>剩余硬直帧。</summary>
        public int StunRemaining => stunRemaining;

        /// <summary>本次硬直总帧数。表现层用 (总−剩余)/总 把受击/防御 clip 硬同步到硬直窗口。</summary>
        public int StunTotalFrames => stunTotal;

        /// <summary>当前受击招式的 MoveId（= 受击 clip 名）。无受击招式时为 null（回退通用受击）。</summary>
        public string CurrentReactionMoveId => reactionMove?.MoveId;

        /// <summary>受击招式已推进的帧号（1 起，随硬直递进）。表现层据此硬同步受击动画。</summary>
        public int ReactionFrame => stunTotal - stunRemaining;

        /// <summary>受击招式总帧数（= 受击 clip 帧数）。</summary>
        public int ReactionTotalFrames => reactionMove?.TotalFrames ?? 0;

        /// <summary>由组合根注入"受击类别 → 受击招式 MoveId"映射（受击招式本身在 Moves 里）。</summary>
        public void SetReactions(Dictionary<HitReaction, string> map) => reactionMoveIds = map;
 
        private int stunRemaining;
        private int stunTotal;      // 本次硬直的总帧数（受击/防御时置入），供表现层硬同步受击动画
        private bool moveConnected; // 当前招是否已命中过（防止多段重复判定，多段招需扩展）

        // ---- 普通技预输入缓冲 ----
        // 搓招指令走 CommandQueue（8 帧缓冲），但裸按键的普通技原本只认 latest.Pressed
        // 的当帧下降沿：在上一招后摇/移动锁定帧里按下的键，等恢复可行动时 Pressed 已归零
        // → 按键被吞，必须松手重按 → 手感卡顿。这里给普通技也补一个短预输入窗口，
        // 在恢复可行动的第一帧回看它，享受与搓招同等的待遇。
        public int NormalBufferFrames = 4;
        private ButtonMask bufferedPress;
        private int bufferedPressFrame;
        // 模拟帧：顿帧中【不推进】。输入缓冲以它计龄，使缓冲"看穿"顿帧——
        // 顿帧里搓的连打不会老化过期，恢复可行动时照常兑现（gatling 连打稳定跟手）。
        private int simFrame;

        // ---- 受击招式（击退位移的载体）----
        // 受击与招式/移动同构：都是带烘焙 RootMotion 的 MoveData，位移逐帧结算。
        // reactionMoveIds: 受击类别 → 受击招式 MoveId（由角色定义注入）；
        // reactionMove:    本次受击命中的具体受击招式，硬直期间驱动其位移与表现。
        private Dictionary<HitReaction, string> reactionMoveIds;
        private MoveData reactionMove;

        // ---- 顿帧(hitstop)：命中瞬间冻结模拟推进的帧数，冲击感来源 ----
        // 由 CollisionResolver 在命中/被防时给攻防双方各置一个。纯 int 状态，进快照即可。
        private int hitstop;
 
        /// <summary>出招瞬间广播——对手侧"看到对方起手"的反应系统订阅这里，而不是去读按键。</summary>
        public event Action<FighterState, MoveData> MoveStarted;

        // ---- 0GC：指令队列的谓词只在构造时分配一次 ----
        // TryAct 中立态每逻辑帧都要 TryPeek 探测队列，谓词若写成捕获 cancelSource/cmd 的
        // lambda，每帧都产生 闭包+委托 两次堆分配（战斗循环唯一的每帧分配源）。
        // 改为字段谓词 + "本次调用参数"过渡字段：单线程逻辑帧内写入即用，无跨帧状态。
        private readonly Predicate<DetectedCommand> commandResolvable;
        private readonly Predicate<DetectedCommand> matchesConsumeId;
        private string pendingCancelSource;
        private CancelKind pendingCancelKind;
        private string pendingConsumeId;

        public FighterState(IInputSeat input, MoveTable moveTable, MovementConfig movementConfig)
        {
            this.input = input;
            this.moveTable = moveTable;
            Movement = new MovementController(movementConfig, input,
                id => moves.TryGetValue(id , out MoveData m) ? m : null);

            // 只捕获 this（一次性分配）；可变参数经 pending* 字段传入
            commandResolvable = c => this.moveTable.ResolveCommand(
                c.Id, this.input.Buffer.Latest.Pressed, CurrentStance,
                pendingCancelSource, pendingCancelKind, this) != null;
            matchesConsumeId = c => c.Id == pendingConsumeId;
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
        public IInputSeat InputController => input;
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
            // 每帧记录最近一次按键下降沿（即使此刻不可行动——锁定帧/顿帧里按的键正是要缓冲的对象）。
            // 用 simFrame 计龄（顿帧中不推进），只在 TryAct 真正可行动时回看；受击时由 ApplyHit 清空。
            ButtonMask pressedNow = input.Buffer.Latest.Pressed;
            if (pressedNow != ButtonMask.None)
            {
                bufferedPress = pressedNow;
                bufferedPressFrame = simFrame;
            }

            // 顿帧：冻结模拟推进（招式帧/移动/硬直倒计时/受击位移全部暂停）。
            // 输入缓冲已在上面照常采集、且 simFrame 不推进 → 顿帧里能预输入且不老化（SF6 手感）。
            // 表现层无需特判：动画硬同步到逻辑帧，帧一冻画面自动定格在冲击帧。
            if (hitstop > 0)
            {
                hitstop--;
                return;
            }

            simFrame++;
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
                    ApplyReactionRootMotion(); // 受击位移（击退）：与招式同一套镜像/逐帧结算
                    stunRemaining--;
                    if (stunRemaining > 0) return;
                    Status = FighterStatus.Neutral;
                    reactionMove = null;
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
        /// 招式进行中的取消判定。两条通道，相位决定走哪条：
        ///
        /// 【前摇 = 变招】Startup 期间（还没打出去）无需命中即可切成【不同】的招——
        /// 武术的试探招/虚招：起手骗对方反应，中途改主意。能否变、变成什么
        /// 完全由招式表 FeintFrom 数据决定（默认不可变，避免重招起手零风险白拉）。
        ///
        /// 【Active/后摇 = 命中取消】当前招【已命中或拼中】且处于取消窗口内时可续招 → 连招。
        /// 未命中（放空）不可取消——这是格斗游戏的基本公平性：空挥要吃后摇。
        /// </summary>
        private void TryCancel()
        {
            if (Phase == MovePhase.Startup)
            {
                TryAct(CurrentMove.MoveId, CancelKind.Feint);
                return;
            }

            if (!moveConnected) return; // 没打中，不给取消
            if (CurrentMove.CancelFrom > 0 && MoveFrame < CurrentMove.CancelFrom) return;

            TryAct(CurrentMove.MoveId, CancelKind.OnHit); // 以当前招为取消来源解析新招
        }

        private void TryAct(string cancelSource, CancelKind cancelKind = CancelKind.None)
        {
            // ① 搓招指令（队列带优先级与预输入窗口）→ 经招式表解析成具体招式。
            // 谓词是构造期缓存的字段（0GC），参数经 pending* 字段传递
            pendingCancelSource = cancelSource;
            pendingCancelKind = cancelKind;
            if (input.Commands.TryPeek(out DetectedCommand cmd, commandResolvable))
            {
                string moveId = moveTable.ResolveCommand(
                    cmd.Id, input.Buffer.Latest.Pressed, CurrentStance, cancelSource, cancelKind, this);
                pendingConsumeId = cmd.Id;
                input.Commands.TryConsume(out _, matchesConsumeId); // 确认可出招后才消费
                StartMove(moveId);
                return;
            }

            InputFrame latest = input.Buffer.Latest;

            // ② 组合键投（LP+LK 同时按下）。投不能从取消/变招里出，只能中立态出
            if (cancelKind == CancelKind.None)
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
 
            // ③ 普通技：裸按键 → 招式表解析（姿态决定 5LP / 2LP / j.LP）。
            // 当帧无新按下时，回看预输入缓冲：上一招后摇/移动锁定帧里按下的键在此兑现，
            // 消除"必须精确压在恢复第一帧、早一帧被吞"的卡顿。命中窗口 NormalBufferFrames。
            ButtonMask press = latest.Pressed;
            if (press == ButtonMask.None
                && bufferedPress != ButtonMask.None
                && simFrame - bufferedPressFrame <= NormalBufferFrames)
            {
                press = bufferedPress;
            }
            if (press != ButtonMask.None)
            {
                string moveId = moveTable.ResolveButton(
                    press, CurrentStance, cancelSource, cancelKind, this);
                if (moveId != null)
                {
                    bufferedPress = ButtonMask.None; // 兑现即消费，避免同一次按下连出两招
                    StartMove(moveId);
                }
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
        /// 回合级重置：新回合开局归位满血，一切回合内状态清零。
        /// 只应由回合系统（BattleSimulation）调用。
        /// 注意 simFrame【不】重置——它只用于给预输入计龄，跨回合单调递增无害，
        /// 而清零反而会让"计龄基准倒退"产生幽灵缓冲。
        /// </summary>
        public void ResetForRound(Vector2 spawnPosition, int health)
        {
            Position = spawnPosition;
            Health = health;
            EndMove();
            CurrentReaction = HitReaction.None;
            reactionMove = null;
            stunRemaining = 0;
            stunTotal = 0;
            hitstop = 0;
            bufferedPress = ButtonMask.None;
            bufferedPressFrame = 0;
            input.Commands.Clear(); // 上回合搓到一半的指令不许穿越回合
            Movement.HardReset();
        }

        // 注：本作【没有防御机制】——原 GuardCheck（按住后方向格挡）已整体移除。
        // 防御的位置由"拼招"取代：双方攻击框相遇即互相抵消（见 CollisionResolver.TestClash）。
        // 按住后方向就只是走位后撤，空间即防御。

        // ---- 由 CollisionResolver 调用的结果施加 ----

        public void MarkMoveConnected() => moveConnected = true;

        /// <summary>
        /// 置入顿帧。取 max：相杀/多段同帧命中不会互相缩短彼此的定格。
        /// </summary>
        public void ApplyHitstop(int frames)
        {
            if (frames > hitstop) hitstop = frames;
        }

        public void ApplyHit(int damage, int hitstunFrames, HitReaction attackReaction = HitReaction.None)
        {
            Health -= damage;
            // 受击类别必须在打断移动【之前】解析——空中/蹲姿判定依赖当前移动与输入状态
            CurrentReaction = ResolveReaction(attackReaction);
            reactionMove = ResolveReactionMove(CurrentReaction); // 受击招式（击退位移的载体）
            EndMove();
            Status = FighterStatus.Hitstun;
            stunRemaining = hitstunFrames;
            stunTotal = hitstunFrames;
            // 受击瞬间清空旧指令：受击前搓好的招作废；硬直期间新搓的会重新入队 → 这正是 reversal
            input.Commands.Clear();
            bufferedPress = ButtonMask.None; // 普通技预输入同样作废（与上面一致）

            // 受击打断移动。但跳跃一旦起跳就必须把整条抛物线播完才能落地——
            // 位置权威是 RootMotion，没有重力兜底，中途 Reset() 不碰 Position，
            // 会把 Y 永久冻在半空（卡在空中）。
            // 判据必须与 MovementController.Tick 的 !IsJumping 完全一致：
            // 用 IsAirborne(=Phase==Active) 会漏掉落地相位(Recovery)里 Y 仍>0 的帧。
            if (!Movement.IsJumping) Movement.Reset();
        }

        public void ApplyBlockstun(int frames)
        {
            EndMove();
            Status = FighterStatus.Blockstun;
            stunRemaining = frames;
            stunTotal = frames;
            reactionMove = null; // 防御没有受击招式（防御位移另说，此处不驱动受击位移）
        }

        /// <summary>受击类别 → 具体受击招式（从已注入的 moves 里取，带烘焙 RootMotion）。</summary>
        private MoveData ResolveReactionMove(HitReaction reaction)
        {
            if (reaction == HitReaction.None || reactionMoveIds == null) return null;
            return reactionMoveIds.TryGetValue(reaction, out string id)
                   && moves.TryGetValue(id, out MoveData m)
                ? m : null;
        }

        /// <summary>
        /// 结算受击招式当前帧的位移（击退）。与 ApplyRootMotion 完全同构：
        /// 位移定义在"面朝右"空间，按当前朝向镜像 X（挨打方朝向攻击者，负位移即被推离）。
        /// index = 已经历的硬直帧数；越界（受击招式位移比硬直短）自然停住，语义正确。
        /// </summary>
        private void ApplyReactionRootMotion()
        {
            Vector2[] motion = reactionMove?.RootMotion;
            if (motion == null) return;

            // 空中被击时位置由跳跃抛物线独占，受击位移不叠加（否则双重位移）。
            // 真正的浮空 juggle 轨迹是单独的待办，届时再让空中受击接管位置。
            if (Movement.IsJumping) return;

            int index = stunTotal - stunRemaining; // 0 起：首个硬直 tick 结算第 1 帧位移
            if (index < 0 || index >= motion.Length) return;

            Vector2 delta = motion[index];
            if (!FacingRight) delta.x = -delta.x;
            Position += delta;
        }

        /// <summary>
        /// 把攻击声明的【基础受击类别】按挨打者自身姿态解析成最终类别：
        /// 空中被击一律 AirHit；蹲姿把站立档降为蹲姿档；
        /// 特殊反应(挑空/扫倒/碎败)与 None 原样透传（它们自带姿态语义或表示"不指定"）。
        /// </summary>
        private HitReaction ResolveReaction(HitReaction attackReaction)
        {
            if (attackReaction == HitReaction.None) return HitReaction.None;

            Stance stance = CurrentStance;
            if (stance == Stance.Airborne) return HitReaction.AirHit;

            if (stance == Stance.Crouching)
            {
                switch (attackReaction)
                {
                    case HitReaction.StandLight:  return HitReaction.CrouchLight;
                    case HitReaction.StandMedium: return HitReaction.CrouchHeavy; // 未细分蹲中档则并入蹲重
                    case HitReaction.StandHeavy:  return HitReaction.CrouchHeavy;
                }
            }
            return attackReaction;
        }
    }
}