using System;
using System.Collections.Generic;
using Domain.Infrastructure.FixedPoint;

namespace Domain.Infrastructure.Battle
{
    public enum MovePhase : byte
    {
        None,
        Startup,   // 前摇：此时被打 = Counter Hit
        Active,    // 判定帧
        Recovery,  // 后摇/恢复：确反窗口，此时被打 = Counter Hit
    }

    /// <summary>
    /// 受击类别——一套【所有角色共享】的反应词表。攻击只声明"造成哪一类反应"，
    /// 由挨打的角色把类别映射到自己的受击 clip（受击动画长在挨打者身上，不在出招者身上）。
    /// 这样避免"每个招配一个专属受击"的组合爆炸：N 个招 × M 个角色不再是 N×M 套 clip。
    ///
    /// 攻击方按【站立地面目标】声明基础类别；挨打方按自身姿态在 ApplyHit 里做二次解析：
    /// 空中 → AirHit；蹲姿 → 对应蹲姿档。特殊反应(挑空/扫倒/碎败)自带姿态语义，原样透传。
    /// </summary>
    public enum HitReaction : byte
    {
        None = 0,     // 不指定：表现层沿用通用受击（向后兼容）
        StandLight,   // 站立轻受击（小顿）
        StandMedium,  // 站立中受击
        StandHeavy,   // 站立重受击（大幅后仰）
        CrouchLight,  // 蹲姿轻受击
        CrouchHeavy,  // 蹲姿重受击
        AirHit,       // 空中被击（浮空继续）
        Launch,       // 挑空：把对方打进浮空
        Sweep,        // 扫倒：击倒在地
        Crumple,      // 碎败：缓慢跪倒（常用于 CH 重击）
    }

    /// <summary>
    /// 攻击属性。克制关系全靠它表达：
    /// 类型位（Strike/Projectile/Throw）决定当身能不能接、投能不能拆；
    /// 位置位（Mid/Low/Overhead）决定站防/蹲防。
    /// </summary>
    [Flags]
    public enum AttackAttribute : ushort
    {
        None       = 0,
        Strike     = 1 << 0, // 打击
        Projectile = 1 << 1, // 飞行道具
        Throw      = 1 << 2, // 投技（不可防，可拆）
        Mid        = 1 << 4, // 中段：站蹲皆可防
        Low        = 1 << 5, // 下段：必须蹲防
        Overhead   = 1 << 6, // 上段/中段打击（Overhead）：必须站防
    }

    /// <summary>
    /// 判定框（AABB），定义在"面朝右"空间：X 为朝向前方的偏移，判定时按朝向自动镜像。
    /// N2 起定点化：判定管线全整数运算，跨语言逐位一致（回滚网络/Go 权威服务器的前提）。
    /// float 构造只准出现在边界——编辑器缺省框与 JSON 关键帧的装载烘焙。
    /// </summary>
    public struct Box
    {
        public Fix X, Y, W, H; // 中心点相对角色原点 + 宽高

        public Box(Fix x, Fix y, Fix w, Fix h)
        {
            X = x; Y = y; W = w; H = h;
        }

        /// <summary>【仅限边界】从浮点创作数据构造（BoxKeyframe.ToBox / 编辑器缺省框）。</summary>
        public Box(float x, float y, float w, float h)
        {
            X = Fix.FromFloat(x); Y = Fix.FromFloat(y);
            W = Fix.FromFloat(w); H = Fix.FromFloat(h);
        }

        public static Box Lerp(Box a, Box b, Fix t) => new Box(
            Fix.Lerp(a.X, b.X, t), Fix.Lerp(a.Y, b.Y, t),
            Fix.Lerp(a.W, b.W, t), Fix.Lerp(a.H, b.H, t));

        public FixRect ToWorld(FixVec2 origin, bool facingRight)
        {
            Fix cx = origin.X + (facingRight ? X : -X);
            Fix cy = origin.Y + Y;
            return FixRect.CenterSize(cx, cy, W, H);
        }
    }

    // 注：原 BoxSpan（手写坐标时代的框区间）已删除——全仓库零引用的死代码，
    // 判定框统一走 BoxTrack 关键帧轨道。

    /// <summary>
    /// 一招的完整帧数据。这是反制系统"状态层"的数据源——
    /// 对方系统读的是【你正在出这招的第几帧、什么阶段】，而不是你的按键。
    /// 目前用代码构造，跑通后应迁移为 ScriptableObject 交给策划配置。
    /// </summary>
    public sealed class MoveData
    {
        public string MoveId;

        // ---- 帧数据（格斗游戏的通用语言）----
        public int Startup;   // 前摇帧数（第 1..Startup 帧）
        public int Active;    // 判定帧数
        public int Recovery;  // 后摇帧数
        public int TotalFrames => Startup + Active + Recovery;

        public AttackAttribute Attributes;
        public int Damage;
        public int HitstunFrames;   // 命中对方的硬直（本作无防御，没有对应的防御硬直字段）
        public int Hitstop;         // 命中顿帧覆盖（0 = 用 CollisionResolver 的结果默认；重招可调大）

        /// <summary>
        /// 命中对方时令其播放的受击类别（按站立地面目标声明；挨打方再按自身姿态二次解析）。
        /// None = 沿用通用受击动画，向后兼容。见 <see cref="HitReaction"/>。
        /// </summary>
        public HitReaction Reaction;

        /// <summary>
        /// 取消窗口起始帧（招式内帧号）。命中/被防后，从该帧起本招可被新招取消 → 连招。
        /// 0 = 判定帧一命中即可取消（最常见）。设为大于判定帧的值可制造"必须晚点取消"的节奏。
        /// 注意：只有命中或被防才开放取消（moveConnected），空挥不可取消——空挥要吃后摇是基本公平性。
        /// </summary>
        public int CancelFrom;

        /// <summary>
        /// 判定框轨道（Hit / Hurt / Push），由 HitboxEditor 可视化编辑、存为 JSON、
        /// 运行时经 BoxDataLoader 注入。关键帧之间自动插值。
        /// 取代了早期手写坐标的 Hitboxes 数组——判定框必须看着动画画，手写做不准。
        /// </summary>
        public List<BoxTrack> BoxTracks = new List<BoxTrack>();

        /// <summary>求本帧所有生效的指定类型的框。复用外部 List 避免 GC。</summary>
        public void CollectBoxes(int moveFrame, BoxKind kind, List<Box> results)
        {
            results.Clear();
            for (int i = 0; i < BoxTracks.Count; i++)
            {
                BoxTrack track = BoxTracks[i];
                if (track.Kind != kind) continue;
                if (track.TryEvaluate(moveFrame, out Box box)) results.Add(box);
            }
        }

        public bool HasBoxes(BoxKind kind)
        {
            for (int i = 0; i < BoxTracks.Count; i++)
                if (BoxTracks[i].Kind == kind) return true;
            return false;
        }

        /// <summary>
        /// 逻辑位移：招式内每帧的根位移增量，定义在"面朝右"空间，index = moveFrame - 1。
        /// null 表示原地招式；数组长度不足的帧视为无位移。
        /// 这是位置的唯一权威来源——Animator 上的 Apply Root Motion 必须取消勾选，
        /// 动画只是这份数据的可视化（傀儡模式，见 FighterView）。
        /// N2 起定点化：JSON 里仍是 float（烘焙产物格式不变），装载时经
        /// BoxDataLoader 一次性转为 FixVec2，此后模拟内全程整数运算。
        /// </summary>
        public FixVec2[] RootMotion;

        // ---- 无敌窗口（升龙第 1~N 帧无敌等），0 = 无 ----
        public int InvulnFrom;
        public int InvulnTo;

        // ---- 当身（接触反击）配置，CatchTo > 0 即视为当身招 ----
        public int CatchFrom;               // 接触窗口起始帧
        public int CatchTo;                 // 接触窗口结束帧
        public AttackAttribute CatchMask;   // 能接住哪些攻击类型（如只接 Strike，接不了投）
        public string CatchFollowupMoveId;  // 接住后自动转入的反击招

        public bool IsCounterStance => CatchTo > 0;

        public MovePhase PhaseAt(int moveFrame)
        {
            if (moveFrame <= 0) return MovePhase.None;
            if (moveFrame <= Startup) return MovePhase.Startup;
            if (moveFrame <= Startup + Active) return MovePhase.Active;
            if (moveFrame <= TotalFrames) return MovePhase.Recovery;
            return MovePhase.None;
        }
    }
}
