package fixed

// Vec2 是 Q16.16 定点二维向量，对齐客户端
// Assets/Domain/Infrastructure/FixedPoint/FixVec2.cs。
// 与 C# 侧一致：不提供归一化/模长（sqrt 是定点最易埋不确定性的地方，核心不用就不写）。
type Vec2 struct {
	X, Y Fix
}

var Vec2Zero = Vec2{Zero, Zero}

func Vec2FromInt(x, y int32) Vec2 { return Vec2{FromInt(x), FromInt(y)} }

// Vec2FromFloat 仅限边界（数据装载）。
func Vec2FromFloat(x, y float32) Vec2 { return Vec2{FromFloat(x), FromFloat(y)} }

func (a Vec2) Add(b Vec2) Vec2 { return Vec2{a.X.Add(b.X), a.Y.Add(b.Y)} }
func (a Vec2) Sub(b Vec2) Vec2 { return Vec2{a.X.Sub(b.X), a.Y.Sub(b.Y)} }
func (a Vec2) Neg() Vec2       { return Vec2{a.X.Neg(), a.Y.Neg()} }

// MulFix 标量缩放（对应 C# 的 FixVec2 * Fix）。
func (a Vec2) MulFix(s Fix) Vec2 { return Vec2{a.X.Mul(s), a.Y.Mul(s)} }

// MirrorX X 取反（"面朝右"空间 → 世界空间的朝向镜像）。
func (a Vec2) MirrorX() Vec2 { return Vec2{a.X.Neg(), a.Y} }

func (a Vec2) Eq(b Vec2) bool { return a.X.Raw == b.X.Raw && a.Y.Raw == b.Y.Raw }

// Vec2Lerp 逐分量线性插值。
func Vec2Lerp(a, b Vec2, t Fix) Vec2 { return Vec2{Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t)} }
