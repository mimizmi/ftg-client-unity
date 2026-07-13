using System;
using System.Collections.Generic;
using UnityEngine;

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
    /// 起步用 float 足够；将来做回滚联网时应换成定点数保证跨机器确定性。
    /// </summary>
    [Serializable]
    public struct Box
    {
        public float X, Y, W, H; // 中心点相对角色原点 + 宽高
 
        public Box(float x, float y, float w, float h)
        {
            X = x; Y = y; W = w; H = h;
        }
 
        public Rect ToWorld(Vector2 origin, bool facingRight)
        {
            float cx = origin.x + (facingRight ? X : -X);
            float cy = origin.y + Y;
            return new Rect(cx - W * 0.5f, cy - H * 0.5f, W, H);
        }
    }
 
    /// <summary>一个判定框的生效帧区间（招式内帧号，1 起始，含端点）。</summary>
    [Serializable]
    public sealed class BoxSpan
    {
        public int FromFrame;
        public int ToFrame;
        public Box Box;
 
        public bool ActiveAt(int moveFrame) => moveFrame >= FromFrame && moveFrame <= ToFrame;
    }
 
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
        public int HitstunFrames;   // 命中对方的硬直
        public int BlockstunFrames; // 被防时对方的防御硬直
 
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
        /// 数据来源二选一：手写 MotionFromSpans(...)（手感优先）；
        /// 或用编辑器工具 RootMotionBaker 从 AnimationClip 按 60Hz 采样烘焙后再手调。
        /// </summary>
        public Vector2[] RootMotion;
 
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