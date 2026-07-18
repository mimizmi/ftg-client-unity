using System;

namespace Domain.Infrastructure.FixedPoint
{
    /// <summary>
    /// Q16.16 定点数：int 承载，高 16 位整数、低 16 位小数（精度 1/65536，范围约 ±32768）。
    /// 【为什么存在】float 跨语言/跨平台不保证逐位一致（x87/SSE、FMA、编译器重排、
    /// Go 与 C# 的 libm 差异），而回滚网络 + Go 权威服务器要求两端模拟结果逐位相同。
    /// 定点数的一切运算都是整数运算——C# 与 Go 的 int32/int64 语义完全一致，天然确定。
    /// 【为什么 Q16.16 而非 Q32.32】乘除的中间量只需 64 位（long 一行搞定，Go 侧 int64
    /// 逐行对应）；±32768 的范围对格斗场地（坐标 ±10 量级、速度 0.0x/帧）绰绰有余。
    /// 【纪律】模拟核心只允许 Fix 运算；FromFloat/ToFloat 只准出现在边界——
    /// 数据装载（JSON→Fix，双方从相同文本解析出相同 double，转换确定）与表现层（Fix→float 渲染）。
    /// 【溢出】unchecked 环绕，不检查——性能优先，靠取值范围设计保证不越界（场地 ±10）。
    /// 【舍入语义】（Go 移植时必须逐条对齐）：
    ///   Mul：64 位乘积算术右移 16 —— 向负无穷舍入；
    ///   Div：左移 16 后整数除法 —— 向零截断（C# 与 Go 的整数除法同为向零截断）；
    ///   RoundToInt：加半后向下取整 —— 0.5 恒向正无穷（不用 banker's rounding）。
    /// </summary>
    public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
    {
        public const int FracBits = 16;
        public const int OneRaw = 1 << FracBits;

        /// <summary>原始承载值（哈希/序列化/网络传输用的就是它，杜绝转换损耗）。</summary>
        public readonly int Raw;

        private Fix(int raw) => Raw = raw;

        public static readonly Fix Zero = new Fix(0);
        public static readonly Fix One = new Fix(OneRaw);
        public static readonly Fix Half = new Fix(OneRaw >> 1);
        public static readonly Fix MaxValue = new Fix(int.MaxValue);
        public static readonly Fix MinValue = new Fix(int.MinValue);

        // ---- 构造（边界转换见纪律注释）----

        public static Fix FromRaw(int raw) => new Fix(raw);

        public static Fix FromInt(int value) => new Fix(value << FracBits);

        /// <summary>分数构造：确定性地表达 3/10 这类常量（比 FromFloat(0.3f) 更明确）。</summary>
        public static Fix FromFraction(int numerator, int denominator)
            => new Fix((int)(((long)numerator << FracBits) / denominator));

        /// <summary>【仅限边界】数据装载用：double 中间量 + 四舍五入，同一文本两端得到同一 Raw。</summary>
        public static Fix FromFloat(float value) => new Fix((int)Math.Round(value * 65536.0));

        /// <summary>【仅限边界】表现层用：模拟内禁止转回 float 参与逻辑。</summary>
        public float ToFloat() => Raw / 65536f;

        /// <summary>向下取整（向负无穷）。</summary>
        public int FloorToInt() => Raw >> FracBits;

        /// <summary>四舍五入：0.5 恒向正无穷（自定义语义，不随平台库变化）。</summary>
        public int RoundToInt() => (Raw + (OneRaw >> 1)) >> FracBits;

        // ---- 算术 ----

        public static Fix operator +(Fix a, Fix b) => new Fix(unchecked(a.Raw + b.Raw));
        public static Fix operator -(Fix a, Fix b) => new Fix(unchecked(a.Raw - b.Raw));
        public static Fix operator -(Fix a) => new Fix(unchecked(-a.Raw));

        public static Fix operator *(Fix a, Fix b)
            => new Fix((int)(((long)a.Raw * b.Raw) >> FracBits));

        public static Fix operator /(Fix a, Fix b)
            => new Fix((int)(((long)a.Raw << FracBits) / b.Raw));

        /// <summary>整数缩放（比先 FromInt 再乘省一次移位，也更常用）。</summary>
        public static Fix operator *(Fix a, int scalar) => new Fix(unchecked(a.Raw * scalar));
        public static Fix operator *(int scalar, Fix a) => a * scalar;
        public static Fix operator /(Fix a, int scalar) => new Fix(a.Raw / scalar);

        // ---- 比较 ----

        public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
        public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
        public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
        public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
        public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

        // ---- 常用函数（覆盖模拟核心当前用到的全部 Mathf 面）----

        public static Fix Abs(Fix a) => a.Raw >= 0 ? a : -a;
        public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;
        public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;

        public static Fix Clamp(Fix value, Fix min, Fix max)
            => value.Raw < min.Raw ? min : value.Raw > max.Raw ? max : value;

        public static Fix Clamp01(Fix value) => Clamp(value, Zero, One);

        /// <summary>线性插值（t 不夹取，语义同 Mathf.LerpUnclamped；调用方按需先 Clamp01）。</summary>
        public static Fix Lerp(Fix a, Fix b, Fix t) => a + (b - a) * t;

        /// <summary>符号：-1 / 0 / +1（朝向翻转等分支用，避免手写 Raw 比较）。</summary>
        public static int Sign(Fix a) => a.Raw > 0 ? 1 : a.Raw < 0 ? -1 : 0;

        // ---- 样板 ----

        public bool Equals(Fix other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fix other && Raw == other.Raw;
        public override int GetHashCode() => Raw;
        public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

        /// <summary>调试显示专用（float 换算仅用于人读，不进逻辑）。</summary>
        public override string ToString() => (Raw / 65536.0).ToString("0.#####");
    }
}
