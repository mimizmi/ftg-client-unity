using Domain.Infrastructure.Input;
using Proto = FTG.Net.Proto;

namespace Domain.Net.Transport
{
    /// <summary>
    /// 内存输入包 ↔ 线上 NetInput 的搬运。proto/ftg/v1/net.proto 是跨语言唯一契约源，
    /// 这里只做字段映射（逐字保真 Direction/Held/Pressed），与 Go 侧 netcode/wire.go 对称。
    /// </summary>
    internal static class NetWire
    {
        public static Proto.NetInput ToNetInput(in InputPacket p) => new Proto.NetInput
        {
            Frame = (uint)p.Frame,
            Input = new Proto.Input
            {
                Direction = p.Input.Direction,
                Held = (uint)p.Input.Held,
                Pressed = (uint)p.Input.Pressed,
            },
        };

        public static InputPacket FromNetInput(Proto.NetInput n)
        {
            var i = n.Input;
            int frame = (int)n.Frame;
            return new InputPacket(frame, new InputFrame
            {
                Frame = frame,
                Direction = (byte)i.Direction,
                Held = (ButtonMask)i.Held,
                Pressed = (ButtonMask)i.Pressed,
                // Released 不过线（可由相邻帧的 Held 差推导）；远端帧无需它驱动模拟。
            });
        }
    }
}
