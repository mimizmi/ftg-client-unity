namespace Domain.Infrastructure.FixedPoint
{
    /// <summary>
    /// 定点 AABB（世界系）：UnityEngine.Rect 在判定管线里的替身。
    /// Overlaps 语义与 Rect.Overlaps 逐字一致：严格不等号，贴边不算相交——
    /// 这条语义同样是 Go 移植的对拍契约之一。
    /// </summary>
    public readonly struct FixRect
    {
        public readonly Fix XMin, YMin, XMax, YMax;

        public FixRect(Fix xMin, Fix yMin, Fix xMax, Fix yMax)
        {
            XMin = xMin;
            YMin = yMin;
            XMax = xMax;
            YMax = yMax;
        }

        public static FixRect CenterSize(Fix cx, Fix cy, Fix w, Fix h)
        {
            Fix hw = w * Fix.Half;
            Fix hh = h * Fix.Half;
            return new FixRect(cx - hw, cy - hh, cx + hw, cy + hh);
        }

        public bool Overlaps(FixRect o)
            => o.XMax > XMin && o.XMin < XMax && o.YMax > YMin && o.YMin < YMax;
    }
}
