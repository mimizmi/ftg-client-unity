using System;
using Domain.Infrastructure.Battle;
using Loxodon.Framework.Messaging;
using UnityEngine;

namespace Domain.Service.Battle
{
    /// <summary>
    /// BattleSimulation 的时钟驱动器：固定步长累加器把渲染帧率解耦成 60Hz 逻辑帧，
    /// 并把模拟事件桥接到表现层（HitEvent → Messenger）。
    /// 模拟本体（状态 + 帧推进）在 BattleSimulation（FTG.Core）——测试与回滚直接驱动那边。
    /// </summary>
    public class BattleLoop : MonoBehaviour
    {
        public const int TickRate = 60;
        private const float TickDelta = 1f / TickRate;
        private const float MaxAccumulated = 0.25f;

        public BattleSimulation Simulation { get; private set; }

        public FighterState P1 => Simulation?.P1;
        public FighterState P2 => Simulation?.P2;
        public CollisionResolver Resolver => Simulation?.Resolver;

        /// <summary>推挡解算：防重叠 + 版边约束。有了移动系统后成为必需。</summary>
        public PushboxResolver Pushbox => Simulation?.Pushbox;

        public int CurrentFrame => Simulation?.CurrentFrame ?? 0;

        /// <summary>
        /// 渲染插值系数：累加器在两个逻辑帧之间的进度 [0,1)。
        /// 表现层（FighterView）用它在上一逻辑帧和当前逻辑帧的位置间插值，
        /// 60Hz 的逻辑在高刷新率屏幕上依然平滑。只读，不参与模拟。
        /// </summary>
        public float InterpolationAlpha => accumulator / TickDelta;

        /// <summary>
        /// 每逻辑帧末尾广播（转发自 BattleSimulation.TickFinished）。
        /// 边界规则：任何要【改动战斗状态】的逻辑（AI、假人自动反应、自动确反）只能挂这里；
        /// 不允许通过 Messenger 改状态——Messenger 是"核心 → 表现层"的单向广播，
        /// 订阅者若反向改状态会破坏帧确定性（回滚网络的前提）。
        /// </summary>
        public event Action<int> TickFinished;

        // 战斗域消息总线（来自 BattleContext）：HitEvent 从这里发布给 HUD/音效/演出
        private Messenger messenger;
        private float accumulator;

        /// <summary>
        /// 由组合根（BattleBootstrap）注入全部依赖，并接管两个输入座位的时钟。
        /// CollisionResolver 与 Messenger 来自战斗上下文，战斗结束随上下文一起释放。
        /// </summary>
        public void Initialize(FighterState p1, FighterState p2,
            CollisionResolver resolver, Messenger battleMessenger)
        {
            Simulation = new BattleSimulation(p1, p2, resolver);
            messenger = battleMessenger;

            Simulation.HitOccurred += ev => messenger?.Publish(ev);
            Simulation.TickFinished += frame => TickFinished?.Invoke(frame);

            p1.InputController.SelfDriven = false;
            p2.InputController.SelfDriven = false;
        }

        private void Update()
        {
            if (Simulation == null) return;

            accumulator += Time.deltaTime;
            if (accumulator > MaxAccumulated) accumulator = MaxAccumulated;

            while (accumulator >= TickDelta)
            {
                accumulator -= TickDelta;
                Simulation.Tick();
            }
        }
    }
}
