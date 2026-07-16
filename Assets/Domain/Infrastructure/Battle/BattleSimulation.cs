using System;
using System.Collections.Generic;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 战斗模拟纯类：一场战斗的全部状态与每帧推进逻辑，零 MonoBehaviour、零渲染依赖。
    /// BattleLoop 只是它的时钟驱动器；EditMode 测试与将来的回滚重模拟直接驱动这里。
    ///
    /// 【帧序不可打乱】朝向 → 输入采样 → 状态推进 → 推挡 → 攻防裁决 → 帧末广播。
    /// 推挡在攻防之前：先把位置解算干净（防重叠、版边约束），
    /// 再用干净的位置做判定——否则判定会读到穿模状态下的错误距离。
    /// </summary>
    public sealed class BattleSimulation
    {
        public FighterState P1 { get; }
        public FighterState P2 { get; }
        public CollisionResolver Resolver { get; }

        /// <summary>推挡解算：防重叠 + 版边约束。</summary>
        public PushboxResolver Pushbox { get; } = new PushboxResolver();

        public int CurrentFrame { get; private set; }

        /// <summary>
        /// 每个命中/拼招事件（帧内顺序确定）。这是"核心 → 表现层"的单向出口：
        /// 订阅者（HUD/音效/演出）不得反向改战斗状态，否则破坏帧确定性。
        /// </summary>
        public event Action<HitEvent> HitOccurred;

        /// <summary>
        /// 每逻辑帧末尾广播。任何要【改动战斗状态】的逻辑（AI、假人自动反应、
        /// 自动确反）只能挂这里——帧内时点确定，重放时可完全复现。
        /// </summary>
        public event Action<int> TickFinished;

        private readonly List<HitEvent> hitEvents = new List<HitEvent>(4);

        public BattleSimulation(FighterState p1, FighterState p2, CollisionResolver resolver)
        {
            P1 = p1;
            P2 = p2;
            Resolver = resolver;
        }

        /// <summary>推进一个逻辑帧（60Hz 语义；调用频率由驱动器负责）。</summary>
        public void Tick()
        {
            CurrentFrame++;

            // ① 朝向同步：位置关系决定朝向，写回角色与输入座位（搓招镜像依赖它）
            bool p1FacesRight = P1.Position.x <= P2.Position.x;
            P1.FacingRight = p1FacesRight;
            P2.FacingRight = !p1FacesRight;
            P1.InputController.FacingRight = p1FacesRight;
            P2.InputController.FacingRight = !p1FacesRight;

            // ② 同帧采样双方输入（内部完成搓招检测与指令入队）
            P1.InputController.ManualTick();
            P2.InputController.ManualTick();

            // ③ 双方状态推进（消费指令、招式帧 +1、移动状态机、硬直倒计时）
            P1.Tick(CurrentFrame);
            P2.Tick(CurrentFrame);

            // ④ 推挡解算
            Pushbox.Resolve(P1, P2);

            // ⑤ 碰撞与攻防裁决（对称检测，支持相杀）
            Resolver.Resolve(CurrentFrame, P1, P2, hitEvents);
            for (int i = 0; i < hitEvents.Count; i++)
                HitOccurred?.Invoke(hitEvents[i]);

            // ⑥ 帧末广播
            TickFinished?.Invoke(CurrentFrame);
        }
    }
}
