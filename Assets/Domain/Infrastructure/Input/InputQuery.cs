namespace Domain.Infrastructure.Input
{
    public static class InputQuery
    {
        /// <summary>
        /// 最近 frames 帧内，dirMask 中的方向是否发生过"刚进入"事件（上一帧不满足、该帧满足）。
        /// 用"进入"而非"存在"，才能表达拒止要求的"轻点"语义——一直按住前不算拒止。
        /// </summary>
        public static bool DirectionEnteredWithin(InputBuffer buffer, ushort dirMask, int frames, bool facingRight)
        {
            for (int a = 0; a < frames; a++)
            {
                if (!buffer.TryGet(a, out InputFrame cur)) return false;
                if (!buffer.TryGet(a + 1, out InputFrame prev)) return false;

                byte d0 = facingRight ? cur.Direction : Numpad.Mirror(cur.Direction);
                byte d1 = facingRight ? prev.Direction : Numpad.Mirror(prev.Direction);

                if ((dirMask & Numpad.Bit(d0)) != 0 && (dirMask & Numpad.Bit(d1)) == 0)
                    return true;
            }
            return false;
        }

        /// <summary>最近 frames 帧内是否有 buttons 中任意键的按下（上升沿）。拆投、假人反应用。</summary>
        public static bool ButtonPressedWithin(InputBuffer buffer, ButtonMask buttons, int frames)
        {
            for (int a = 0; a < frames; a++)
            {
                if (!buffer.TryGet(a, out InputFrame f)) return false;
                if ((f.Pressed & buttons) != 0) return true;
            }
            return false;
        }

        /// <summary>dirMask 方向是否已被连续保持至少 frames 帧（防御保持、蓄力类检查）。</summary>
        public static bool WasHolding(InputBuffer buffer, ushort dirMask, int frames, bool facingRight)
        {
            for (int a = 0; a < frames; a++)
            {
                if (!buffer.TryGet(a, out InputFrame f)) return false;
                byte d = facingRight ? f.Direction : Numpad.Mirror(f.Direction);
                if ((dirMask & Numpad.Bit(d)) == 0) return false;
            }
            return true;
        }
    }
}