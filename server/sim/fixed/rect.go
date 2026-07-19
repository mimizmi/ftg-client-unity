package fixed

// Rect 是定点 AABB（世界系），对齐客户端
// Assets/Domain/Infrastructure/FixedPoint/FixRect.cs。
// Overlaps 语义与 Unity Rect.Overlaps 逐字一致：严格不等号，贴边不算相交——
// 这条语义是跨语言对拍契约之一。
type Rect struct {
	XMin, YMin, XMax, YMax Fix
}

// RectCenterSize 由中心点 + 宽高构造（半宽半高用 *Half，与 C# 一致）。
func RectCenterSize(cx, cy, w, h Fix) Rect {
	hw := w.Mul(Half)
	hh := h.Mul(Half)
	return Rect{cx.Sub(hw), cy.Sub(hh), cx.Add(hw), cy.Add(hh)}
}

// Overlaps 严格不等号：贴边不算相交。
func (r Rect) Overlaps(o Rect) bool {
	return o.XMax.Gt(r.XMin) && o.XMin.Lt(r.XMax) && o.YMax.Gt(r.YMin) && o.YMin.Lt(r.YMax)
}
