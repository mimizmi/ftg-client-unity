using System.Collections.Generic;
using Domain.Infrastructure.Input;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum DefenseOutcome : byte
    {
        Hit,            // 普通命中
        CounterHit,     // 对方出招中被打（前摇/后摇）→ 硬直加成，确反奖励
        Blocked,        // 防御成立
        Parried,        // 拒止：碰撞帧回看到守方精确输入
        CounterCaught,  // 被当身接住：攻守互换
        Thrown,         // 被投
        ThrowTeched,    // 拆投成功
    }

    public sealed class HitEvent
    {
        public int Frame;
        public FighterState Attacker;
        public FighterState Defender;
        public MoveData Move;
        public DefenseOutcome Outcome;

        public override string ToString() =>
            $"[帧{Frame}] {Attacker.Name}:{Move.MoveId} → {Defender.Name} = {Outcome}";
    }

    /// <summary>
    /// 碰撞与攻防裁决。每逻辑帧在双方状态推进之后调用一次。
    ///
    /// 关键设计：所有"反制规则"集中在 Judge() 这一条按优先级排列的管线里：
    ///     ① 无敌 → ② 当身 → ③ 投/拆投 → ④ 拒止 → ⑤ 防御 → ⑥ 命中（含 CH 判定）
    /// 顺序即规则（例：当身排在拒止前 = 当身状态下不会触发拒止）。
    /// 加新反制机制 = 在管线里插一层，而不是在角色代码里到处埋 if。
    ///
    /// "精确到帧"的三种落点在这里全部体现：
    /// - 拒止：碰撞成立帧，回看【守方自己】的 InputBuffer 最近 N 帧（InputQuery）
    /// - 当身：读【攻方招式】的属性 + 【守方招式】的当前帧是否在接触窗口内（状态层）
    /// - CH：读【守方招式】的 Phase 是否为 Startup/Recovery（状态层）
    /// </summary>
    public sealed class CollisionResolver
    {
        // ---- 拒止配置（做成配置便于手感调优/按角色差异化）----
        public bool ParryEnabled = true;
        public int ParryWindow = 8;        // 碰撞前 N 帧内轻点方向有效
        public bool ParryForward = true;   // true=前拒止(SF3 式)，false=后拒止(JD 式)
        public int ThrowTechWindow = 5;    // 拆投窗口
        public float CounterHitDamageScale = 1.2f;
        public int CounterHitBonusStun = 8;

        public void Resolve(int frame, FighterState p1, FighterState p2, List<HitEvent> results)
        {
            results.Clear();

            // 先对称检测再统一施加：同帧互中 = 相杀(trade)，两边都吃结果
            bool p1HitsP2 = TestOverlap(p1, p2);
            bool p2HitsP1 = TestOverlap(p2, p1);

            if (p1HitsP2) results.Add(Judge(frame, p1, p2));
            if (p2HitsP1) results.Add(Judge(frame, p2, p1));

            for (int i = 0; i < results.Count; i++)
                Apply(results[i]);
        }

        private readonly List<Box> attackBoxes = new List<Box>(4);
        private readonly List<Box> defendBoxes = new List<Box>(4);

        /// <summary>
        /// 攻击框 vs 受击框。两者都来自 BoxTracks（可视化编辑、关键帧插值），
        /// 受击框随动画姿态变化——蹲下时框变矮，这是"上段打空、下段命中"的机制基础。
        /// </summary>
        private bool TestOverlap(FighterState attacker, FighterState defender)
        {
            if (!attacker.CanMoveConnect) return false;
            if (defender.IsInvulnerable) return false; // ① 无敌：升龙无敌帧穿招

            attacker.CurrentMove.CollectBoxes(attacker.MoveFrame, BoxKind.Hit, attackBoxes);
            if (attackBoxes.Count == 0) return false;

            defender.CollectHurtboxes(defendBoxes);
            if (defendBoxes.Count == 0) return false;

            for (int i = 0; i < attackBoxes.Count; i++)
            {
                Rect hit = attackBoxes[i].ToWorld(attacker.Position, attacker.FacingRight);
                for (int j = 0; j < defendBoxes.Count; j++)
                {
                    Rect hurt = defendBoxes[j].ToWorld(defender.Position, defender.FacingRight);
                    if (hit.Overlaps(hurt)) return true;
                }
            }
            return false;
        }

        private HitEvent Judge(int frame, FighterState attacker, FighterState defender)
        {
            MoveData move = attacker.CurrentMove;
            attacker.MarkMoveConnected();

            var ev = new HitEvent { Frame = frame, Attacker = attacker, Defender = defender, Move = move };

            // ② 当身：守方处于接触窗口，且来击类型在可接范围内（投通常接不住）
            if (defender.CounterCatchActive
                && (move.Attributes & defender.CurrentMove.CatchMask) != 0)
            {
                ev.Outcome = DefenseOutcome.CounterCaught;
                return ev;
            }

            // ③ 投：不可防、不可拒止，但可拆——回看被投方最近 N 帧是否按过投
            if ((move.Attributes & AttackAttribute.Throw) != 0)
            {
                bool teched = defender.Status == FighterStatus.Neutral
                    && InputQuery.ButtonPressedWithin(
                        defender.InputHistory, ButtonMask.LP | ButtonMask.LK, ThrowTechWindow);
                // 注：严谨版是投成立后进入 N 帧"待拆"状态再裁决（允许碰撞之后才按）。
                // 回看式是常见简化，先跑通手感再升级。
                ev.Outcome = teched ? DefenseOutcome.ThrowTeched : DefenseOutcome.Thrown;
                return ev;
            }

            // ④ 拒止："帧精确参考按键"的标准落点——碰撞帧回看守方自己的输入缓冲
            if (ParryEnabled
                && defender.Status == FighterStatus.Neutral
                && (move.Attributes & (AttackAttribute.Strike | AttackAttribute.Projectile)) != 0)
            {
                ushort parryDir = ParryForward ? Numpad.Mask(6) : Numpad.Mask(4);
                if (InputQuery.DirectionEnteredWithin(
                        defender.InputHistory, parryDir, ParryWindow, defender.FacingRight))
                {
                    ev.Outcome = DefenseOutcome.Parried;
                    return ev;
                }
            }

            // ⑤ 防御：方向档位 vs 攻击位置属性
            if (defender.GuardCheck(move.Attributes))
            {
                ev.Outcome = DefenseOutcome.Blocked;
                return ev;
            }

            // ⑥ 命中。CH 判定 = 守方正处于自己招式的前摇/后摇（读状态层，不读按键）
            bool counterHit =
                (defender.Status == FighterStatus.Attacking
                    && (defender.Phase == MovePhase.Startup || defender.Phase == MovePhase.Recovery))
                || defender.Status == FighterStatus.CounterStance; // 当身窗外/接不住的类型被打也算 CH

            ev.Outcome = counterHit ? DefenseOutcome.CounterHit : DefenseOutcome.Hit;
            return ev;
        }

        private void Apply(HitEvent ev)
        {
            MoveData move = ev.Move;
            switch (ev.Outcome)
            {
                case DefenseOutcome.Hit:
                    ev.Defender.ApplyHit(move.Damage, move.HitstunFrames);
                    break;

                case DefenseOutcome.CounterHit:
                    ev.Defender.ApplyHit(
                        Mathf.RoundToInt(move.Damage * CounterHitDamageScale),
                        move.HitstunFrames + CounterHitBonusStun);
                    break;

                case DefenseOutcome.Blocked:
                    ev.Defender.ApplyBlockstun(move.BlockstunFrames);
                    break;

                case DefenseOutcome.Parried:
                    // 守方无伤无硬直，攻方照常收招 → 天然产生巨大确反窗口。
                    // 真实游戏还有冻结帧(freeze)与拒止演出，在表现层做。
                    break;

                case DefenseOutcome.CounterCaught:
                    // 攻守互换：攻方吃大硬直，守方自动转入反击招
                    ev.Attacker.ApplyHit(0, 30);
                    if (!string.IsNullOrEmpty(ev.Defender.CurrentMove?.CatchFollowupMoveId))
                        ev.Defender.StartMove(ev.Defender.CurrentMove.CatchFollowupMoveId);
                    break;

                case DefenseOutcome.Thrown:
                    ev.Defender.ApplyHit(move.Damage, move.HitstunFrames);
                    break;

                case DefenseOutcome.ThrowTeched:
                    // 双方小硬直分开（推开位移在表现/物理层做）
                    ev.Attacker.ApplyBlockstun(12);
                    ev.Defender.ApplyBlockstun(12);
                    break;
            }
        }
    }
}