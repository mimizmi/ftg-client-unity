namespace Domain.Service.Battle
{
    /// <summary>
    /// 表现层时钟：把「当前逻辑帧」与「两逻辑帧间的插值进度」抽象出来，让 FighterView 既能跟随
    /// 单机 BattleLoop，也能跟随在线回滚驱动（NetworkBattleBootstrap）——两者都按 60Hz 推进逻辑帧、
    /// 都在渲染帧之间插值，视图不必知道背后是单机模拟还是回滚预测。
    /// </summary>
    public interface IPresentationClock
    {
        /// <summary>当前逻辑帧号（单调递增）。视图据其变化决定何时刷新插值端点与动画。</summary>
        int CurrentFrame { get; }

        /// <summary>累加器在两个逻辑帧之间的进度 [0,1)。视图用它插值，60Hz 逻辑在高刷屏上依旧平滑。</summary>
        float InterpolationAlpha { get; }
    }
}
