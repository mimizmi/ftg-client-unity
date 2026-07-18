using Domain.Infrastructure.FixedPoint;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// Fix/FixVec2 的语义契约测试：每条断言同时是 Go 移植时的对拍基准——
    /// 舍入方向、截断语义、边界行为在两端必须逐条一致。
    /// </summary>
    public sealed class FixTests
    {
        // ---- 构造与转换 ----

        [Test]
        public void FromInt_And_FromFloat_ProduceExpectedRaw()
        {
            Assert.AreEqual(Fix.OneRaw, Fix.FromInt(1).Raw);
            Assert.AreEqual(-3 * Fix.OneRaw, Fix.FromInt(-3).Raw);
            Assert.AreEqual(32768, Fix.FromFloat(0.5f).Raw);
            Assert.AreEqual(-32768, Fix.FromFloat(-0.5f).Raw);
            Assert.AreEqual(Fix.OneRaw, Fix.FromFloat(1.0f).Raw);
            Assert.AreEqual(0, Fix.FromFloat(0f).Raw);
        }

        [Test]
        public void FromFraction_IsExactForRepresentableValues()
        {
            Assert.AreEqual(Fix.Half, Fix.FromFraction(1, 2));
            Assert.AreEqual(Fix.FromInt(3), Fix.FromFraction(6, 2));
            // 1/3 不可精确表示：向零截断到最近的 1/65536 刻度
            Assert.AreEqual(21845, Fix.FromFraction(1, 3).Raw); // floor(65536/3)
            Assert.AreEqual(-21845, Fix.FromFraction(-1, 3).Raw); // 整数除法向零截断
        }

        [Test]
        public void FloorToInt_TowardNegativeInfinity_RoundToInt_HalfUp()
        {
            Assert.AreEqual(1, Fix.FromFloat(1.7f).FloorToInt());
            Assert.AreEqual(-2, Fix.FromFloat(-1.3f).FloorToInt()); // 向负无穷，不是向零

            Assert.AreEqual(2, Fix.FromFloat(1.5f).RoundToInt());   // 0.5 向正无穷
            Assert.AreEqual(-1, Fix.FromFloat(-1.5f).RoundToInt()); // -1.5 + 0.5 = -1
            Assert.AreEqual(0, Fix.FromFloat(-0.5f).RoundToInt());
            Assert.AreEqual(3, Fix.FromInt(3).RoundToInt());
        }

        // ---- 算术语义（Go 对拍基准）----

        [Test]
        public void AddSub_AreExact()
        {
            Fix a = Fix.FromFloat(1.25f), b = Fix.FromFloat(2.5f);
            Assert.AreEqual(Fix.FromFloat(3.75f), a + b);
            Assert.AreEqual(Fix.FromFloat(-1.25f), a - b);
            Assert.AreEqual(Fix.FromFloat(-1.25f), -(b - a));
        }

        [Test]
        public void Mul_ExactCases_AndSignCombinations()
        {
            Assert.AreEqual(Fix.FromInt(3), Fix.FromFloat(1.5f) * Fix.FromInt(2));
            Assert.AreEqual(Fix.FromFloat(0.25f), Fix.Half * Fix.Half);
            Assert.AreEqual(Fix.FromInt(-6), Fix.FromInt(2) * Fix.FromInt(-3));
            Assert.AreEqual(Fix.FromInt(6), Fix.FromInt(-2) * Fix.FromInt(-3));
            Assert.AreEqual(Fix.Zero, Fix.Zero * Fix.MaxValue);
        }

        [Test]
        public void Mul_ShiftRoundsTowardNegativeInfinity()
        {
            // 最小正数相乘下溢出为 0；负方向下溢出到 -1/65536（算术右移向负无穷）
            Fix epsilon = Fix.FromRaw(1);
            Assert.AreEqual(0, (epsilon * epsilon).Raw);
            Assert.AreEqual(-1, (-epsilon * epsilon).Raw);
        }

        [Test]
        public void Div_ExactCases_AndTruncationTowardZero()
        {
            Assert.AreEqual(Fix.Half, Fix.One / Fix.FromInt(2));
            Assert.AreEqual(Fix.FromFloat(-0.75f), Fix.FromFloat(1.5f) / Fix.FromInt(-2));
            // 不可整除：向零截断（C# 与 Go 的整数除法语义一致）
            Assert.AreEqual(-1, (Fix.FromRaw(-3) / Fix.FromInt(2)).Raw);
            Assert.AreEqual(1, (Fix.FromRaw(3) / Fix.FromInt(2)).Raw);
        }

        [Test]
        public void IntScalar_MulDiv()
        {
            Assert.AreEqual(Fix.FromInt(9), Fix.FromInt(3) * 3);
            Assert.AreEqual(Fix.FromFloat(1.5f), 3 * Fix.Half);
            Assert.AreEqual(Fix.FromFloat(0.75f), Fix.FromFloat(1.5f) / 2);
        }

        // ---- 比较与常用函数 ----

        [Test]
        public void Comparisons_And_MinMaxAbsSign()
        {
            Fix a = Fix.FromFloat(-1.5f), b = Fix.Half;
            Assert.IsTrue(a < b && b > a && a <= a && b >= b && a != b);
            Assert.AreEqual(a, Fix.Min(a, b));
            Assert.AreEqual(b, Fix.Max(a, b));
            Assert.AreEqual(Fix.FromFloat(1.5f), Fix.Abs(a));
            Assert.AreEqual(b, Fix.Abs(b));
            Assert.AreEqual(-1, Fix.Sign(a));
            Assert.AreEqual(1, Fix.Sign(b));
            Assert.AreEqual(0, Fix.Sign(Fix.Zero));
        }

        [Test]
        public void Clamp_And_Clamp01()
        {
            Assert.AreEqual(Fix.FromInt(2), Fix.Clamp(Fix.FromInt(5), Fix.Zero, Fix.FromInt(2)));
            Assert.AreEqual(Fix.Zero, Fix.Clamp(Fix.FromInt(-5), Fix.Zero, Fix.FromInt(2)));
            Assert.AreEqual(Fix.One, Fix.Clamp01(Fix.FromInt(7)));
            Assert.AreEqual(Fix.Zero, Fix.Clamp01(Fix.FromFloat(-0.3f)));
            Assert.AreEqual(Fix.Half, Fix.Clamp01(Fix.Half));
        }

        [Test]
        public void Lerp_EndpointsAndMidpoint_AndUnclamped()
        {
            Fix a = Fix.FromInt(2), b = Fix.FromInt(6);
            Assert.AreEqual(a, Fix.Lerp(a, b, Fix.Zero));
            Assert.AreEqual(b, Fix.Lerp(a, b, Fix.One));
            Assert.AreEqual(Fix.FromInt(4), Fix.Lerp(a, b, Fix.Half));
            Assert.AreEqual(Fix.FromInt(10), Fix.Lerp(a, b, Fix.FromInt(2))); // 不夹取
        }

        // ---- 确定性：同一运算序列两次执行 Raw 逐位一致 ----

        [Test]
        public void OperationSequence_IsBitExactAcrossRuns()
        {
            Assert.AreEqual(RunSequence(), RunSequence());
        }

        private static int RunSequence()
        {
            // LCG 驱动的伪随机运算流：任何未来对 Fix 语义的改动都会改变这个折叠值
            uint seed = 12345;
            Fix acc = Fix.One;
            int hash = 17;
            for (int i = 0; i < 1000; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                Fix operand = Fix.FromRaw((int)(seed % 200000) - 100000);
                switch (seed % 4)
                {
                    case 0: acc += operand; break;
                    case 1: acc -= operand; break;
                    case 2: acc = acc * Fix.FromRaw((int)(seed % 131072)); break;
                    default:
                        if (operand.Raw != 0) acc /= operand;
                        break;
                }
                acc = Fix.Clamp(acc, Fix.FromInt(-30000), Fix.FromInt(30000));
                hash = unchecked(hash * 31 + acc.Raw);
            }
            return hash;
        }

        // ---- FixVec2 ----

        [Test]
        public void FixVec2_Arithmetic_And_Equality()
        {
            FixVec2 a = FixVec2.FromInt(1, 2);
            FixVec2 b = FixVec2.FromFloat(0.5f, -1.5f);

            Assert.AreEqual(FixVec2.FromFloat(1.5f, 0.5f), a + b);
            Assert.AreEqual(FixVec2.FromFloat(0.5f, 3.5f), a - b);
            Assert.AreEqual(FixVec2.FromFloat(-0.5f, 1.5f), -b);
            Assert.AreEqual(FixVec2.FromInt(2, 4), a * Fix.FromInt(2));
            Assert.AreEqual(FixVec2.FromFloat(0.25f, -0.75f), Fix.Half * b);
            Assert.IsTrue(a != b && a == FixVec2.FromInt(1, 2));
            Assert.AreEqual(FixVec2.Zero, a - a);
        }

        [Test]
        public void FixVec2_Lerp()
        {
            FixVec2 a = FixVec2.FromInt(0, 0);
            FixVec2 b = FixVec2.FromInt(4, -8);
            Assert.AreEqual(FixVec2.FromInt(2, -4), FixVec2.Lerp(a, b, Fix.Half));
            Assert.AreEqual(b, FixVec2.Lerp(a, b, Fix.One));
        }
    }
}
