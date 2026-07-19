// Package combat 是确定性战斗核心的 Go 移植，对齐客户端
// Assets/Domain/Infrastructure/Battle 下的判定框/招式数据与结算逻辑。
// 一切数值走 fixed.Fix 定点；float 只出现在 JSON 装载边界（BoxKeyframe）。
package combat

import "ftgserver/sim/fixed"

// Box 是判定框（AABB），定义在"面朝右"空间：X 为朝向前方的偏移，判定时按朝向镜像。
// 对齐 C# MoveData.cs 的 Box 结构。
type Box struct {
	X, Y, W, H fixed.Fix // 中心点相对角色原点 + 宽高
}

// BoxFromFloat 仅限边界：从浮点创作数据构造（BoxKeyframe.ToBox）。
func BoxFromFloat(x, y, w, h float32) Box {
	return Box{fixed.FromFloat(x), fixed.FromFloat(y), fixed.FromFloat(w), fixed.FromFloat(h)}
}

// BoxLerp 逐分量线性插值（关键帧之间补帧）。
func BoxLerp(a, b Box, t fixed.Fix) Box {
	return Box{
		fixed.Lerp(a.X, b.X, t),
		fixed.Lerp(a.Y, b.Y, t),
		fixed.Lerp(a.W, b.W, t),
		fixed.Lerp(a.H, b.H, t),
	}
}

// ToWorld 把"面朝右"空间的框按朝向镜像并平移到世界系 AABB。
func (b Box) ToWorld(origin fixed.Vec2, facingRight bool) fixed.Rect {
	var cx fixed.Fix
	if facingRight {
		cx = origin.X.Add(b.X)
	} else {
		cx = origin.X.Sub(b.X)
	}
	cy := origin.Y.Add(b.Y)
	return fixed.RectCenterSize(cx, cy, b.W, b.H)
}

// BoxKeyframe 是判定框的一个关键帧【JSON 创作数据 DTO，保持 float】，
// 对齐 C# BoxData.cs 的 BoxKeyframe。运行时判定用的定点框由 BoxTrack 惰性烘焙。
type BoxKeyframe struct {
	Frame      int // 招式内帧号（1 起始）
	X, Y, W, H float32
}

// ToBox 是 float→Fix 的边界转换点。
func (k BoxKeyframe) ToBox() Box { return BoxFromFloat(k.X, k.Y, k.W, k.H) }

// BoxTrack 是一条判定框轨道：一个框在整个招式期间的生命史。对齐 C# BoxTrack。
// 定点关键帧惰性烘焙：运行时数据加载后不变，首次判定烘焙一次终身使用。
// 指针接收者（对齐 C# 的 class 引用语义），MoveData 持有 []*BoxTrack。
type BoxTrack struct {
	Kind      BoxKind
	FromFrame int // 生效起始帧（含）
	ToFrame   int // 结束帧（含）
	Keys      []BoxKeyframe
	baked     []Box // 惰性缓存，与 Keys 一一对应
}

func (t *BoxTrack) ActiveAt(moveFrame int) bool {
	return moveFrame >= t.FromFrame && moveFrame <= t.ToFrame
}

func (t *BoxTrack) bake() {
	t.baked = make([]Box, len(t.Keys))
	for i, k := range t.Keys {
		t.baked[i] = k.ToBox()
	}
}

// TryEvaluate 求某帧的框（运行时判定路径，全定点）。关键帧间插值；越界钳到端点。
func (t *BoxTrack) TryEvaluate(moveFrame int) (Box, bool) {
	if !t.ActiveAt(moveFrame) || len(t.Keys) == 0 {
		return Box{}, false
	}
	if t.baked == nil || len(t.baked) != len(t.Keys) {
		t.bake()
	}

	if len(t.Keys) == 1 || moveFrame <= t.Keys[0].Frame {
		return t.baked[0], true
	}

	last := len(t.Keys) - 1
	if moveFrame >= t.Keys[last].Frame {
		return t.baked[last], true
	}

	for i := 0; i < last; i++ {
		a := t.Keys[i]
		b := t.Keys[i+1]
		if moveFrame < a.Frame || moveFrame > b.Frame {
			continue
		}
		span := b.Frame - a.Frame
		var tt fixed.Fix
		if span <= 0 {
			tt = fixed.Zero
		} else {
			tt = fixed.FromFraction(int32(moveFrame-a.Frame), int32(span))
		}
		return BoxLerp(t.baked[i], t.baked[i+1], tt), true
	}
	return Box{}, false
}
