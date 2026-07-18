using System;

namespace Domain.Infrastructure.FixedPoint
{
    /// <summary>
    /// 定点二维向量：Vector2 在模拟核心里的替身（位置/速度/位移增量）。
    /// 只提供模拟实际用到的运算——没有归一化/模长（当前核心无此需求，
    /// sqrt 是定点实现里最容易埋不确定性的地方，不用就不写）。
    /// </summary>
    public readonly struct FixVec2 : IEquatable<FixVec2>
    {
        public readonly Fix X;
        public readonly Fix Y;

        public FixVec2(Fix x, Fix y)
        {
            X = x;
            Y = y;
        }

        public static readonly FixVec2 Zero = new FixVec2(Fix.Zero, Fix.Zero);

        public static FixVec2 FromInt(int x, int y) => new FixVec2(Fix.FromInt(x), Fix.FromInt(y));

        /// <summary>【仅限边界】数据装载用（JSON→定点）。</summary>
        public static FixVec2 FromFloat(float x, float y)
            => new FixVec2(Fix.FromFloat(x), Fix.FromFloat(y));

        /// <summary>X 取反（"面朝右"空间数据 → 世界空间的朝向镜像，位移/判定通用）。</summary>
        public FixVec2 MirrorX() => new FixVec2(-X, Y);

        public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
        public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
        public static FixVec2 operator -(FixVec2 a) => new FixVec2(-a.X, -a.Y);
        public static FixVec2 operator *(FixVec2 a, Fix scalar) => new FixVec2(a.X * scalar, a.Y * scalar);
        public static FixVec2 operator *(Fix scalar, FixVec2 a) => a * scalar;

        public static bool operator ==(FixVec2 a, FixVec2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(FixVec2 a, FixVec2 b) => !(a == b);

        public static FixVec2 Lerp(FixVec2 a, FixVec2 b, Fix t)
            => new FixVec2(Fix.Lerp(a.X, b.X, t), Fix.Lerp(a.Y, b.Y, t));

        public bool Equals(FixVec2 other) => this == other;
        public override bool Equals(object obj) => obj is FixVec2 other && this == other;
        public override int GetHashCode() => (X.Raw * 397) ^ Y.Raw;
        public override string ToString() => $"({X}, {Y})";
    }
}
