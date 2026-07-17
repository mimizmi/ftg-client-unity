using System.Collections.Generic;
using Domain.Infrastructure.Input;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum DefenseOutcome : byte
    {
        Hit,            // 普通命中
        CounterHit,     // 对方出招中被打（前摇/后摇）→ 硬直加成，确反奖励
        Clashed,        // 拼招：双方攻击框相遇，两招互相抵消（本作无防御，这就是"格挡"）
        Parried,        // 拒止：碰撞帧回看到守方精确输入（默认关闭）
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

        /// <summary>
        /// 接触点（世界系）：首个相交的 攻击框∩受击框（拼招则为双方攻击框）交集矩形中心。
        /// 由判定当场算出——盒数据的确定性函数。表现层（火花/演出）按这里生成，别再估。
        /// </summary>
        public Vector2 ContactPoint;

        public override string ToString() =>
            $"[帧{Frame}] {Attacker.Name}:{Move.MoveId} → {Defender.Name} = {Outcome}";
    }

    /// <summary>
    /// 碰撞与攻防裁决。每逻辑帧在双方状态推进之后调用一次。
    ///
    /// 本作【没有防御】：按住后方向只是走位，空间即防御。"格挡"的位置由拼招取代——
    /// 双方攻击框相遇即两招互相抵消（Resolve 顶部的 ⓪ 层，优先于一切命中裁决）。
    ///
    /// 关键设计：所有"反制规则"集中在一条按优先级排列的管线里：
    ///     ⓪ 拼招 → ① 无敌 → ② 当身 → ③ 投/拆投 → ④ 拒止(默认关) → ⑤ 命中(含 CH 判定)
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
        // ---- 拼招(clash)配置：无防御设计的核心 ----
        public bool ClashEnabled = true;
        public int ClashHitstop = 16;      // 拼招定格：兵刃相接的演出重量，比普通命中更重

        // ---- 拒止配置（属防御系机制，与"无防御"设计冲突 → 默认关；想试 SF3 手感再开）----
        public bool ParryEnabled = false;
        public int ParryWindow = 8;        // 碰撞前 N 帧内轻点方向有效
        public bool ParryForward = true;   // true=前拒止(SF3 式)，false=后拒止(JD 式)
        public int ThrowTechWindow = 5;    // 拆投窗口
        public float CounterHitDamageScale = 1.2f;
        public int CounterHitBonusStun = 8;

        // ---- 顿帧(hitstop)：命中定格帧数。命中是主手调旋钮，CH 由它派生 ----
        public int HitHitstop = 8;              // 命中顿帧：盯这一个数手调（脆↔沉）
        public int CounterHitBonus = 4;         // CH = 命中 + 此（确反重奖）
        public int ParryHitstop = 16;           // 拒止定格自成一档（SF3 式闪光演出）

        public void Resolve(int frame, FighterState p1, FighterState p2, List<HitEvent> results)
        {
            results.Clear();

            // ⓪ 拼招：双方攻击框相遇 → 两招互相抵消 + 双方定格 + 双方取消窗同开。
            // 本帧不再裁定命中（拼招覆盖一切——即使同帧攻击框也扫到了对方身体）。
            if (ClashEnabled && TestClash(p1, p2, out Vector2 clashPoint))
            {
                var clash = new HitEvent
                {
                    Frame = frame, Attacker = p1, Defender = p2,
                    Move = p1.CurrentMove, Outcome = DefenseOutcome.Clashed,
                    ContactPoint = clashPoint,
                };
                results.Add(clash);
                Apply(clash);
                return;
            }

            // 先对称检测再统一施加：同帧互中 = 相杀(trade)，两边都吃结果
            bool p1HitsP2 = TestOverlap(p1, p2, out Vector2 p1Contact);
            bool p2HitsP1 = TestOverlap(p2, p1, out Vector2 p2Contact);

            if (p1HitsP2) results.Add(Judge(frame, p1, p2, p1Contact));
            if (p2HitsP1) results.Add(Judge(frame, p2, p1, p2Contact));

            for (int i = 0; i < results.Count; i++)
                Apply(results[i]);
        }

        private readonly List<Box> attackBoxes = new List<Box>(4);
        private readonly List<Box> defendBoxes = new List<Box>(4);

        /// <summary>
        /// 拼招检测：双方同时处于判定期（Active 且本招尚未命中）且攻击框相互重叠。
        /// 只有打击技参与拼招——投是贴身抓取，没有"兵刃相接"的语义。
        /// 拼招事件的 Attacker/Defender 只是记名（p1/p2），双方地位完全对等。
        /// </summary>
        private bool TestClash(FighterState a, FighterState b, out Vector2 contact)
        {
            contact = default;
            if (!a.CanMoveConnect || !b.CanMoveConnect) return false;
            if ((a.CurrentMove.Attributes & AttackAttribute.Strike) == 0) return false;
            if ((b.CurrentMove.Attributes & AttackAttribute.Strike) == 0) return false;

            a.CurrentMove.CollectBoxes(a.MoveFrame, BoxKind.Hit, attackBoxes);
            if (attackBoxes.Count == 0) return false;
            b.CurrentMove.CollectBoxes(b.MoveFrame, BoxKind.Hit, defendBoxes);
            if (defendBoxes.Count == 0) return false;

            for (int i = 0; i < attackBoxes.Count; i++)
            {
                Rect ra = attackBoxes[i].ToWorld(a.Position, a.FacingRight);
                for (int j = 0; j < defendBoxes.Count; j++)
                {
                    Rect rb = defendBoxes[j].ToWorld(b.Position, b.FacingRight);
                    if (ra.Overlaps(rb))
                    {
                        contact = IntersectionCenter(ra, rb);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 攻击框 vs 受击框。两者都来自 BoxTracks（可视化编辑、关键帧插值），
        /// 受击框随动画姿态变化——蹲下时框变矮，这是"上段打空、下段命中"的机制基础。
        /// </summary>
        private bool TestOverlap(FighterState attacker, FighterState defender, out Vector2 contact)
        {
            contact = default;
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
                    if (hit.Overlaps(hurt))
                    {
                        contact = IntersectionCenter(hit, hurt);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>两矩形交集的中心。仅在已确认 Overlaps 后调用（交集必非空）。</summary>
        private static Vector2 IntersectionCenter(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float yMax = Mathf.Min(a.yMax, b.yMax);
            return new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
        }

        private HitEvent Judge(int frame, FighterState attacker, FighterState defender, Vector2 contact)
        {
            MoveData move = attacker.CurrentMove;
            attacker.MarkMoveConnected();

            var ev = new HitEvent
            {
                Frame = frame, Attacker = attacker, Defender = defender,
                Move = move, ContactPoint = contact,
            };

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

            // ⑤ 命中。本作没有防御分支——没拼上、没拒止、没无敌就是挨打。
            // CH 判定 = 守方正处于自己招式的前摇/后摇（读状态层，不读按键）
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
            ApplyHitstop(ev); // 攻防双方同时定格（冲击感）；投/拆投/当身 v1 不加
            switch (ev.Outcome)
            {
                case DefenseOutcome.Hit:
                    ev.Defender.ApplyHit(move.Damage, move.HitstunFrames, move.Reaction);
                    break;

                case DefenseOutcome.CounterHit:
                    ev.Defender.ApplyHit(
                        Mathf.RoundToInt(move.Damage * CounterHitDamageScale),
                        move.HitstunFrames + CounterHitBonusStun, move.Reaction);
                    break;

                case DefenseOutcome.Clashed:
                    // 两招互相消耗（moveConnected 置位 → 本招不再能命中），同时这正是
                    // 命中取消的门闩——拼中即视为"接上了"，双方都可立刻取消续招/变招，
                    // 谁反应快谁抢下一手。无伤、无硬直，定格在 ApplyHitstop 统一给。
                    ev.Attacker.MarkMoveConnected();
                    ev.Defender.MarkMoveConnected();
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
                    ev.Defender.ApplyHit(move.Damage, move.HitstunFrames, move.Reaction);
                    break;

                case DefenseOutcome.ThrowTeched:
                    // 双方小硬直分开（推开位移在表现/物理层做）
                    ev.Attacker.ApplyBlockstun(12);
                    ev.Defender.ApplyBlockstun(12);
                    break;
            }
        }

        /// <summary>
        /// 按结果给攻防双方置入顿帧。命中/CH 可被 MoveData.Hitstop 覆盖；
        /// 拼招/拒止用各自默认；投/拆投/当身 v1 不加顿帧（有各自演出流程）。
        /// </summary>
        private void ApplyHitstop(HitEvent ev)
        {
            int hit = ev.Move.Hitstop > 0 ? ev.Move.Hitstop : HitHitstop; // 招式可覆盖命中顿帧
            int frames;
            switch (ev.Outcome)
            {
                case DefenseOutcome.Hit:        frames = hit; break;
                case DefenseOutcome.CounterHit: frames = hit + CounterHitBonus; break;
                case DefenseOutcome.Clashed:    frames = ClashHitstop; break;
                case DefenseOutcome.Parried:    frames = ParryHitstop; break;
                default: return; // Thrown / ThrowTeched / CounterCaught：v1 不加顿帧
            }

            ev.Attacker.ApplyHitstop(frames);
            ev.Defender.ApplyHitstop(frames);
        }
    }
}