using System;
using System.Text;

namespace Domain.Infrastructure.Input
{
    [Flags]
    public enum ButtonMask : byte
    {
        None = 0,
        LP = 1 << 0,
        MP = 1 << 1,
        HP = 1 << 2,
        LK = 1 << 3,
        MK = 1 << 4,
        HK = 1 << 5,
    }

    public struct InputFrame
    {
        public int Frame;
        public byte Direction;
        public ButtonMask Held;
        public ButtonMask Pressed;
        public ButtonMask Released;
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Direction);
            if (Held != ButtonMask.None) sb.Append('+').Append(Held);
            return sb.ToString();
        }
    }

    public static class Numpad
    {
        public static byte FromAxes(int dx, int dy)
        {
            return (byte)((dy + 1) * 3 + (dx + 1) + 1);
        }

        public static byte Mirror(byte dir)
        {
            switch (dir)
            {
                case 1: return 3;
                case 3: return 1;
                case 4: return 6;
                case 6: return 4;
                case 7: return 9;
                case 9: return 7;
                default: return dir;
            }
        }

        public static ushort Bit(byte dir) => (ushort)(1 << dir);

        public static ushort Mask(params byte[] dirs)
        {
            ushort m = 0;
            foreach (var t in dirs)
                m |= Bit(t);

            return m;
        }
    }
}