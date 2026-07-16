namespace Domain.Infrastructure.Input
{
    /// <summary>
    /// 一个"座位"的输入源：模拟核心（FighterState/MovementController）只透过这个
    /// 接口读输入，不知道输入从哪来——键盘（FightingInputController）、脚本化序列
    /// （EditMode 测试/训练模式假人）、录像回放、还是将来的网络远端帧，都可注入。
    /// 这是回滚网络"输入即状态"纪律的接口化落点。
    /// </summary>
    public interface IInputSeat
    {
        /// <summary>逐帧输入历史环形缓冲。模拟只读。</summary>
        InputBuffer Buffer { get; }

        /// <summary>搓招检测产物队列（带缓冲期）。模拟消费。</summary>
        CommandQueue Commands { get; }

        /// <summary>朝向（影响搓招镜像与方向语义），由战斗循环每帧回写。</summary>
        bool FacingRight { get; set; }

        /// <summary>true = 自驱采样（menu 等无战斗循环场景）；战斗中由 BattleLoop 关掉改为 ManualTick。</summary>
        bool SelfDriven { get; set; }

        /// <summary>由战斗循环在逻辑帧内驱动一次输入采样（确定性：采样点与模拟帧对齐）。</summary>
        void ManualTick();
    }
}
