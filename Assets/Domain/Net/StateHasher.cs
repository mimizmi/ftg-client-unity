using Domain.Infrastructure.Battle;

namespace Domain.Net
{
    /// <summary>
    /// 帧状态哈希的【规范实现】——跨语言对拍（N4）的契约。
    ///
    /// 算法：FNV-1a（64 位），按【固定字段顺序】折叠双方角色的确定性状态。
    /// 字段集与顺序 = EditMode 确定性测试内联的 HashFighter 完全一致
    /// （见 BattleSimulationTests / TestBattleFactory）。之所以在这里再落一份：
    ///   · 测试那份只被引用 FTG.Core 的测试程序集看得到；
    ///   · 对拍的哈希是【协议契约】，必须住在产品代码里、被 Go 侧逐字节镜像。
    /// 两份算法字面相同，`ProtoCodecTests` 有一条断言钉死它们一致（改一处即红）。
    ///
    /// 定点化后哈希直接吃 Fix.Raw（int）——没有 float 位模式的平台歧义，这正是 N1/N2 的意义。
    /// Go 侧移植要点：uint 折叠按小端逐字节；字符串按 char 的 uint 值逐个折叠；
    /// null MoveId 折叠哨兵 0xFFFFFFFF。
    /// </summary>
    public static class StateHasher
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong HashState(BattleSimulation sim)
        {
            ulong h = OffsetBasis;
            h = HashFighter(h, sim.P1);
            h = HashFighter(h, sim.P2);
            return h;
        }

        private static ulong HashFighter(ulong h, FighterState f)
        {
            h = Fnv(h, (uint)f.Position.X.Raw);
            h = Fnv(h, (uint)f.Position.Y.Raw);
            h = Fnv(h, (uint)f.Health);
            h = Fnv(h, (byte)f.Status);
            h = Fnv(h, (uint)f.MoveFrame);
            h = Fnv(h, (uint)f.StunRemaining);
            h = Fnv(h, (byte)f.Movement.State);
            h = Fnv(h, (uint)f.Movement.MotionFrame);
            h = Fnv(h, f.FacingRight ? 1u : 0u);
            h = FnvString(h, f.CurrentMove?.MoveId);
            return h;
        }

        private static ulong Fnv(ulong h, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(value >> (i * 8));
                h *= Prime;
            }
            return h;
        }

        private static ulong FnvString(ulong h, string s)
        {
            if (s == null) return Fnv(h, 0xFFFFFFFFu);
            for (int i = 0; i < s.Length; i++)
                h = Fnv(h, s[i]);
            return h;
        }
    }
}
