package fixed

import "testing"

// Fix/Vec2 语义契约的 Go 侧镜像，与客户端 Assets/Tests/EditMode/FixTests.cs 逐条对应。
// 每条手算断言就是跨语言对拍基准：舍入方向、截断语义、边界行为两端必须逐位一致。

func eqRaw(t *testing.T, want, got int32, msg string) {
	t.Helper()
	if want != got {
		t.Errorf("%s: 期望 Raw=%d, 实际 %d", msg, want, got)
	}
}

func eqFix(t *testing.T, want, got Fix, msg string) {
	t.Helper()
	if want.Raw != got.Raw {
		t.Errorf("%s: 期望 %d, 实际 %d", msg, want.Raw, got.Raw)
	}
}

func TestFromInt_And_FromFloat(t *testing.T) {
	eqRaw(t, OneRaw, FromInt(1).Raw, "FromInt(1)")
	eqRaw(t, -3*OneRaw, FromInt(-3).Raw, "FromInt(-3)")
	eqRaw(t, 32768, FromFloat(0.5).Raw, "FromFloat(0.5)")
	eqRaw(t, -32768, FromFloat(-0.5).Raw, "FromFloat(-0.5)")
	eqRaw(t, OneRaw, FromFloat(1.0).Raw, "FromFloat(1.0)")
	eqRaw(t, 0, FromFloat(0).Raw, "FromFloat(0)")
}

func TestFromFraction_IsExact(t *testing.T) {
	eqFix(t, Half, FromFraction(1, 2), "1/2")
	eqFix(t, FromInt(3), FromFraction(6, 2), "6/2")
	eqRaw(t, 21845, FromFraction(1, 3).Raw, "1/3 floor(65536/3)")
	eqRaw(t, -21845, FromFraction(-1, 3).Raw, "-1/3 向零截断")
}

func TestFloor_And_Round(t *testing.T) {
	eqRaw(t, 1, FromFloat(1.7).FloorToInt(), "floor(1.7)")
	eqRaw(t, -2, FromFloat(-1.3).FloorToInt(), "floor(-1.3) 向负无穷")

	eqRaw(t, 2, FromFloat(1.5).RoundToInt(), "round(1.5) 向正无穷")
	eqRaw(t, -1, FromFloat(-1.5).RoundToInt(), "round(-1.5) = -1")
	eqRaw(t, 0, FromFloat(-0.5).RoundToInt(), "round(-0.5) = 0")
	eqRaw(t, 3, FromInt(3).RoundToInt(), "round(3) = 3")
}

func TestAddSub_AreExact(t *testing.T) {
	a, b := FromFloat(1.25), FromFloat(2.5)
	eqFix(t, FromFloat(3.75), a.Add(b), "1.25+2.5")
	eqFix(t, FromFloat(-1.25), a.Sub(b), "1.25-2.5")
	eqFix(t, FromFloat(-1.25), b.Sub(a).Neg(), "-(2.5-1.25)")
}

func TestMul_ExactCases(t *testing.T) {
	eqFix(t, FromInt(3), FromFloat(1.5).Mul(FromInt(2)), "1.5*2")
	eqFix(t, FromFloat(0.25), Half.Mul(Half), "0.5*0.5")
	eqFix(t, FromInt(-6), FromInt(2).Mul(FromInt(-3)), "2*-3")
	eqFix(t, FromInt(6), FromInt(-2).Mul(FromInt(-3)), "-2*-3")
	eqFix(t, Zero, Zero.Mul(MaxValue), "0*max")
}

func TestMul_ShiftTowardNegInfinity(t *testing.T) {
	epsilon := FromRaw(1)
	eqRaw(t, 0, epsilon.Mul(epsilon).Raw, "eps*eps 下溢为 0")
	eqRaw(t, -1, epsilon.Neg().Mul(epsilon).Raw, "-eps*eps 向负无穷到 -1")
}

func TestDiv_TruncationTowardZero(t *testing.T) {
	eqFix(t, Half, One.Div(FromInt(2)), "1/2")
	eqFix(t, FromFloat(-0.75), FromFloat(1.5).Div(FromInt(-2)), "1.5/-2")
	eqRaw(t, -1, FromRaw(-3).Div(FromInt(2)).Raw, "-3/2 向零截断")
	eqRaw(t, 1, FromRaw(3).Div(FromInt(2)).Raw, "3/2 向零截断")
}

func TestIntScalar_MulDiv(t *testing.T) {
	eqFix(t, FromInt(9), FromInt(3).MulInt(3), "3*3")
	eqFix(t, FromFloat(1.5), Half.MulInt(3), "3*0.5")
	eqFix(t, FromFloat(0.75), FromFloat(1.5).DivInt(2), "1.5/2")
}

func TestComparisons_And_MinMaxAbsSign(t *testing.T) {
	a, b := FromFloat(-1.5), Half
	if !(a.Lt(b) && b.Gt(a) && a.Le(a) && b.Ge(b) && !a.Eq(b)) {
		t.Error("比较运算不符预期")
	}
	eqFix(t, a, Min(a, b), "Min")
	eqFix(t, b, Max(a, b), "Max")
	eqFix(t, FromFloat(1.5), Abs(a), "Abs(-1.5)")
	eqFix(t, b, Abs(b), "Abs(0.5)")
	if Sign(a) != -1 || Sign(b) != 1 || Sign(Zero) != 0 {
		t.Error("Sign 不符预期")
	}
}

func TestClamp_And_Clamp01(t *testing.T) {
	eqFix(t, FromInt(2), Clamp(FromInt(5), Zero, FromInt(2)), "clamp 上界")
	eqFix(t, Zero, Clamp(FromInt(-5), Zero, FromInt(2)), "clamp 下界")
	eqFix(t, One, Clamp01(FromInt(7)), "clamp01 上界")
	eqFix(t, Zero, Clamp01(FromFloat(-0.3)), "clamp01 下界")
	eqFix(t, Half, Clamp01(Half), "clamp01 中间")
}

func TestLerp_EndpointsMidpoint_Unclamped(t *testing.T) {
	a, b := FromInt(2), FromInt(6)
	eqFix(t, a, Lerp(a, b, Zero), "lerp t=0")
	eqFix(t, b, Lerp(a, b, One), "lerp t=1")
	eqFix(t, FromInt(4), Lerp(a, b, Half), "lerp t=0.5")
	eqFix(t, FromInt(10), Lerp(a, b, FromInt(2)), "lerp 不夹取")
}

// 确定性：同一伪随机运算流两次执行折叠值逐位一致（与 C# RunSequence 同构）。
func TestOperationSequence_IsBitExact(t *testing.T) {
	first, second := runSequence(), runSequence()
	if first != second {
		t.Error("同一运算序列两次执行结果不一致——存在非确定性源")
	}
}

func runSequence() int32 {
	var seed uint32 = 12345
	acc := One
	var hash int32 = 17
	for range 1000 {
		seed = seed*1664525 + 1013904223
		operand := FromRaw(int32(seed%200000) - 100000)
		switch seed % 4 {
		case 0:
			acc = acc.Add(operand)
		case 1:
			acc = acc.Sub(operand)
		case 2:
			acc = acc.Mul(FromRaw(int32(seed % 131072)))
		default:
			if operand.Raw != 0 {
				acc = acc.Div(operand)
			}
		}
		acc = Clamp(acc, FromInt(-30000), FromInt(30000))
		hash = hash*31 + acc.Raw
	}
	return hash
}

// ---- Vec2 ----

func TestVec2_Arithmetic_And_Equality(t *testing.T) {
	a := Vec2FromInt(1, 2)
	b := Vec2FromFloat(0.5, -1.5)

	if !a.Add(b).Eq(Vec2FromFloat(1.5, 0.5)) {
		t.Error("a+b 不符")
	}
	if !a.Sub(b).Eq(Vec2FromFloat(0.5, 3.5)) {
		t.Error("a-b 不符")
	}
	if !b.Neg().Eq(Vec2FromFloat(-0.5, 1.5)) {
		t.Error("-b 不符")
	}
	if !a.MulFix(FromInt(2)).Eq(Vec2FromInt(2, 4)) {
		t.Error("a*2 不符")
	}
	if !b.MulFix(Half).Eq(Vec2FromFloat(0.25, -0.75)) {
		t.Error("0.5*b 不符")
	}
	if a.Eq(b) || !a.Eq(Vec2FromInt(1, 2)) {
		t.Error("相等判断不符")
	}
	if !a.Sub(a).Eq(Vec2Zero) {
		t.Error("a-a 应为零")
	}
}

func TestVec2_Lerp(t *testing.T) {
	a := Vec2FromInt(0, 0)
	b := Vec2FromInt(4, -8)
	if !Vec2Lerp(a, b, Half).Eq(Vec2FromInt(2, -4)) {
		t.Error("vec2 lerp 中点不符")
	}
	if !Vec2Lerp(a, b, One).Eq(b) {
		t.Error("vec2 lerp t=1 不符")
	}
}
