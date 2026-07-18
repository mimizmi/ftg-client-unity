using System.Collections.Generic;
using Domain.Infrastructure.FixedPoint;

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
    ///
    /// N2 定点化：重叠量/边界钳制全整数运算（此处正是"各推一半"的 ×0.5——
    /// 定点乘 Fix.Half 的舍入语义固定为向负无穷，跨语言一致）。
    /// </summary>
    public sealed class PushboxResolver
    {
        private readonly List<Box> boxesA = new List<Box>(2);
        private readonly List<Box> boxesB = new List<Box>(2);

        /// <summary>场地左右边界（世界坐标，定点）</summary>
        public Fix StageLeft = Fix.FromInt(-3);
        public Fix StageRight = Fix.FromInt(3);

        /// <summary>角色最大分离距离。超过这个距离摄像机/场地会限制（暂未实现，留接口）</summary>
        public Fix MaxSeparation = Fix.FromInt(8);

        public void Resolve(FighterState p1, FighterState p2)
        {
            ResolveOverlap(p1, p2);
            ClampToStage(p1);
            ClampToStage(p2);
            // 版边再解算一次：贴边后可能又产生重叠，需要把对方推开
            ResolveOverlapAgainstWall(p1, p2);
        }

        private void ResolveOverlap(FighterState a, FighterState b)
        {
            // 空中角色不参与推挡——这是格斗游戏的通行设定，否则跳过对方头顶会被卡住
            if (a.Movement.IsAirborne || b.Movement.IsAirborne) return;

            if (!TryGetOverlap(a, b, out Fix overlap)) return;

            // 各推开一半（对称，无优先方）
            Fix half = overlap * Fix.Half;
            Fix shift = a.Position.X <= b.Position.X ? -half : half;

            a.Position = new FixVec2(a.Position.X + shift, a.Position.Y);
            b.Position = new FixVec2(b.Position.X - shift, b.Position.Y);
        }

        /// <summary>取两人推挡框的水平重叠量。框全部来自 JSON，不再有硬编码的"身体柱子"。</summary>
        private bool TryGetOverlap(FighterState a, FighterState b, out Fix overlap)
        {
            overlap = Fix.Zero;
            a.CollectPushboxes(boxesA);
            b.CollectPushboxes(boxesB);
            if (boxesA.Count == 0 || boxesB.Count == 0) return false;

            for (int i = 0; i < boxesA.Count; i++)
            {
                FixRect ra = boxesA[i].ToWorld(a.Position, a.FacingRight);
                for (int j = 0; j < boxesB.Count; j++)
                {
                    FixRect rb = boxesB[j].ToWorld(b.Position, b.FacingRight);
                    if (!ra.Overlaps(rb)) continue;

                    Fix o = Fix.Min(ra.XMax, rb.XMax) - Fix.Max(ra.XMin, rb.XMin);
                    if (o > overlap) overlap = o;
                }
            }
            return overlap > Fix.Zero;
        }

        private void ClampToStage(FighterState f)
        {
            f.CollectPushboxes(boxesA);
            if (boxesA.Count == 0) return;

            // 用最宽的推挡框做边界约束
            Fix halfW = Fix.Zero;
            foreach (Box b in boxesA)
                halfW = Fix.Max(halfW, b.W * Fix.Half);

            Fix clampedX = Fix.Clamp(f.Position.X, StageLeft + halfW, StageRight - halfW);
            f.Position = new FixVec2(clampedX, f.Position.Y);
        }

        /// <summary>
        /// 版边传导：一方被夹在版边和对方之间时，重叠量全部推给对方。
        /// 这是"角落压制"的物理基础——被压在角落的人退无可退，
        /// 进攻方可以持续贴身，这是格斗游戏最核心的空间博弈。
        /// </summary>
        private void ResolveOverlapAgainstWall(FighterState a, FighterState b)
        {
            if (a.Movement.IsAirborne || b.Movement.IsAirborne) return;
            if (!TryGetOverlap(a, b, out Fix overlap)) return;

            // 判断谁贴着版边，把重叠量全推给另一方
            bool aAtWall = IsAtWall(a);
            bool bAtWall = IsAtWall(b);

            if (aAtWall && !bAtWall)
            {
                Fix shift = a.Position.X <= b.Position.X ? overlap : -overlap;
                b.Position = new FixVec2(b.Position.X + shift, b.Position.Y);
            }
            else if (bAtWall && !aAtWall)
            {
                Fix shift = b.Position.X <= a.Position.X ? overlap : -overlap;
                a.Position = new FixVec2(a.Position.X + shift, a.Position.Y);
            }
        }

        private bool IsAtWall(FighterState f)
        {
            f.CollectPushboxes(boxesA);
            if (boxesA.Count == 0) return false;

            // 原 0.001f 的浮点容差 → 定点 1/1000（Raw 65），语义不变
            Fix epsilon = Fix.FromFraction(1, 1000);
            foreach (Box b in boxesA)
            {
                FixRect r = b.ToWorld(f.Position, f.FacingRight);
                if (r.XMin <= StageLeft + epsilon || r.XMax >= StageRight - epsilon)
                    return true;
            }
            return false;
        }
    }
}
