using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 推挡解算：防止两角色重叠，并把角色约束在场地内。
    ///
    /// 有了移动系统之后这块立刻变成必需——否则两人可以直接穿模站在同一格。
    /// 它是碰撞管线的独立一环，在攻防裁决【之前】执行：先把位置解算干净，
    /// 再用干净的位置去判定攻击命中，否则判定会读到穿模状态下的错误距离。
    ///
    /// 版边传导：一方贴住版边时，另一方推过来会被反向推开——这是"角落压制"
    /// 这一格斗游戏核心概念的物理基础（被压在角落的人退无可退）。
    /// </summary>
    public sealed class PushboxResolver
    {
        /// <summary>场地左右边界（世界坐标）</summary>
        public float StageLeft = -6f;
        public float StageRight = 6f;

        /// <summary>角色最大分离距离。超过这个距离摄像机/场地会限制（暂未实现，留接口）</summary>
        public float MaxSeparation = 8f;

        public void Resolve(FighterState p1, FighterState p2)
        {
            ResolveOverlap(p1, p2);
            ClampToStage(p1);
            ClampToStage(p2);
            // 版边再解算一次：贴边后可能又产生重叠，需要把对方推开
            ResolveOverlapAgainstWall(p1, p2);
        }

        private static void ResolveOverlap(FighterState a, FighterState b)
        {
            // 空中角色不参与推挡——这是格斗游戏的通行设定，否则跳过对方头顶会被卡住
            if (a.Movement.IsAirborne || b.Movement.IsAirborne) return;

            Rect ra = a.PushBox.ToWorld(a.Position, a.FacingRight);
            Rect rb = b.PushBox.ToWorld(b.Position, b.FacingRight);
            if (!ra.Overlaps(rb)) return;

            // 水平重叠量
            float overlap = Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin);
            if (overlap <= 0f) return;

            // 各推开一半（对称，无优先方）
            float half = overlap * 0.5f;
            float dir = a.Position.x <= b.Position.x ? -1f : 1f;

            a.Position.x += dir * half;
            b.Position.x -= dir * half;
        }

        private void ClampToStage(FighterState f)
        {
            Rect box = f.PushBox.ToWorld(f.Position, f.FacingRight);
            float halfW = box.width * 0.5f;

            float min = StageLeft + halfW;
            float max = StageRight - halfW;
            f.Position.x = Mathf.Clamp(f.Position.x, min, max);
        }

        /// <summary>
        /// 版边传导：一方被夹在版边和对方之间时，重叠量全部推给对方。
        /// 这是"角落压制"的物理基础——被压在角落的人退无可退，
        /// 进攻方可以持续贴身，这是格斗游戏最核心的空间博弈。
        /// </summary>
        private void ResolveOverlapAgainstWall(FighterState a, FighterState b)
        {
            if (a.Movement.IsAirborne || b.Movement.IsAirborne) return;

            Rect ra = a.PushBox.ToWorld(a.Position, a.FacingRight);
            Rect rb = b.PushBox.ToWorld(b.Position, b.FacingRight);
            if (!ra.Overlaps(rb)) return;

            float overlap = Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin);
            if (overlap <= 0f) return;

            // 判断谁贴着版边，把重叠量全推给另一方
            bool aAtWall = IsAtWall(a, ra);
            bool bAtWall = IsAtWall(b, rb);

            if (aAtWall && !bAtWall)
            {
                float dir = a.Position.x <= b.Position.x ? 1f : -1f;
                b.Position.x += dir * overlap;
            }
            else if (bAtWall && !aAtWall)
            {
                float dir = b.Position.x <= a.Position.x ? 1f : -1f;
                a.Position.x += dir * overlap;
            }
        }

        private bool IsAtWall(FighterState f, Rect box)
        {
            const float epsilon = 0.001f;
            return box.xMin <= StageLeft + epsilon || box.xMax >= StageRight - epsilon;
        }
    }
}