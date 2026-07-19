// Package fixed 是 Q16.16 定点数的 Go 移植，与客户端
// Assets/Domain/Infrastructure/FixedPoint/Fix.cs 逐位一致——这是回滚网络 + Go 权威
// 服务器要求两端模拟结果逐位相同的地基。
//
// 移植红线：
//
//	· Raw 用 int32（C# int 是 32 位）。Go 的有符号整数溢出是良定义的环绕，
//	  与 C# unchecked 语义一致；移位在 int32 上做，保证与 C# 相同的截断/环绕。
//	· Mul/Div 的中间量升到 int64（对应 C# 的 long），算完再截回 int32。
//	· FromFloat 取 float32 参数（C# 是 float，乘 65536.0 时提升到 double），
//	  用 RoundToEven（C# Math.Round 默认 banker's rounding）——连非精确值也逐位对齐。
//	· 舍入契约：Mul 算术右移向负无穷；Div 整数除法向零截断；RoundToInt 加半向下取整。
package fixed

import "math"

const (
	FracBits = 16
	OneRaw   = 1 << FracBits // 65536
)

// Fix 是 Q16.16 定点数：int32 承载，高 16 位整数、低 16 位小数。
type Fix struct{ Raw int32 }

// ---- 构造（边界转换见包注释）----

func FromRaw(raw int32) Fix { return Fix{raw} }

func FromInt(v int32) Fix { return Fix{v << FracBits} }

// FromFraction 确定性地表达 3/10 这类常量（向零截断到最近的 1/65536 刻度）。
func FromFraction(num, den int32) Fix {
	return Fix{int32((int64(num) << FracBits) / int64(den))}
}

// FromFloat 仅限边界（数据装载）。float32 参数 + 提升到 float64 相乘，镜像 C# 的 float→double。
func FromFloat(v float32) Fix { return Fix{int32(math.RoundToEven(float64(v) * 65536.0))} }

var (
	Zero     = Fix{0}
	One      = Fix{OneRaw}
	Half     = Fix{OneRaw >> 1}
	MaxValue = Fix{math.MaxInt32}
	MinValue = Fix{math.MinInt32}
)

// ---- 转换 ----

// ToFloat 仅限边界（表现层）。
func (a Fix) ToFloat() float64 { return float64(a.Raw) / 65536.0 }

// FloorToInt 向下取整（向负无穷）——int32 算术右移。
func (a Fix) FloorToInt() int32 { return a.Raw >> FracBits }

// RoundToInt 四舍五入：0.5 恒向正无穷。
func (a Fix) RoundToInt() int32 { return (a.Raw + (OneRaw >> 1)) >> FracBits }

// ---- 算术 ----

func (a Fix) Add(b Fix) Fix { return Fix{a.Raw + b.Raw} }
func (a Fix) Sub(b Fix) Fix { return Fix{a.Raw - b.Raw} }
func (a Fix) Neg() Fix      { return Fix{-a.Raw} }

func (a Fix) Mul(b Fix) Fix { return Fix{int32((int64(a.Raw) * int64(b.Raw)) >> FracBits)} }
func (a Fix) Div(b Fix) Fix { return Fix{int32((int64(a.Raw) << FracBits) / int64(b.Raw))} }

// MulInt/DivInt：整数缩放（对应 C# 的 Fix*int / Fix/int 重载）。
func (a Fix) MulInt(s int32) Fix { return Fix{a.Raw * s} }
func (a Fix) DivInt(s int32) Fix { return Fix{a.Raw / s} }

// ---- 比较（直接比 Raw；提供命名方法便于表达）----

func (a Fix) Lt(b Fix) bool { return a.Raw < b.Raw }
func (a Fix) Gt(b Fix) bool { return a.Raw > b.Raw }
func (a Fix) Le(b Fix) bool { return a.Raw <= b.Raw }
func (a Fix) Ge(b Fix) bool { return a.Raw >= b.Raw }
func (a Fix) Eq(b Fix) bool { return a.Raw == b.Raw }

// ---- 常用函数（覆盖模拟核心用到的全部 Mathf 面）----

func Abs(a Fix) Fix {
	if a.Raw >= 0 {
		return a
	}
	return a.Neg()
}

func Min(a, b Fix) Fix {
	if a.Raw <= b.Raw {
		return a
	}
	return b
}

func Max(a, b Fix) Fix {
	if a.Raw >= b.Raw {
		return a
	}
	return b
}

func Clamp(v, lo, hi Fix) Fix {
	if v.Raw < lo.Raw {
		return lo
	}
	if v.Raw > hi.Raw {
		return hi
	}
	return v
}

func Clamp01(v Fix) Fix { return Clamp(v, Zero, One) }

// Lerp 线性插值（t 不夹取，同 Mathf.LerpUnclamped）。
func Lerp(a, b, t Fix) Fix { return a.Add(b.Sub(a).Mul(t)) }

// Sign 符号：-1 / 0 / +1。
func Sign(a Fix) int32 {
	if a.Raw > 0 {
		return 1
	}
	if a.Raw < 0 {
		return -1
	}
	return 0
}
