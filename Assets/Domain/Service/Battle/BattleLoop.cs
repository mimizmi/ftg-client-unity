using System;
using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Loxodon.Framework.Messaging;
using UnityEngine;

namespace Domain.Service.Battle
{
    public class BattleLoop : MonoBehaviour
    {
        public const int TickRate = 60;
        private const float TickDelta = 1f / TickRate;
        private const float MaxAccumulated = 0.25f;
        public FighterState P1 { get; private set; }
        public FighterState P2 { get; private set; }
        public CollisionResolver Resolver { get; private set; }

        /// <summary>推挡解算：防重叠 + 版边约束。有了移动系统后成为必需。</summary>
        public PushboxResolver Pushbox { get; } = new PushboxResolver();

        public int CurrentFrame { get; private set; }

        /// <summary>
        /// 渲染插值系数：累加器在两个逻辑帧之间的进度 [0,1)。
        /// 表现层（FighterView）用它在上一逻辑帧和当前逻辑帧的位置间插值，
        /// 60Hz 的逻辑在高刷新率屏幕上依然平滑。只读，不参与模拟。
        /// </summary>
        public float InterpolationAlpha => accumulator / TickDelta;

        /// <summary>
        /// 每逻辑帧末尾广播（C# 事件，帧内顺序确定）。
        /// 边界规则：任何要【改动战斗状态】的逻辑（AI、假人自动反应、自动确反）只能挂这里；
        /// 不允许通过 Messenger 改状态——Messenger 是"核心 → 表现层"的单向广播，
        /// 订阅者若反向改状态会破坏帧确定性（回滚网络的前提）。
        /// </summary>
        public event Action<int> TickFinished;

        // 战斗域消息总线（来自 BattleContext）：HitEvent 从这里发布给 HUD/音效/演出
        private Messenger messenger;

        private readonly List<HitEvent> hitEvents = new List<HitEvent>(4);
        private float accumulator;

        /// <summary>
        /// 由组合根（BattleBootstrap）注入全部依赖，并接管两个输入控制器的时钟。
        /// BattleLoop 不再自己 new 服务：CollisionResolver 与 Messenger 来自战斗上下文，
        /// 战斗结束随上下文一起释放。
        /// </summary>
        public void Initialize(FighterState p1, FighterState p2,
            CollisionResolver resolver, Messenger battleMessenger)
        {
            P1 = p1;
            P2 = p2;
            Resolver = resolver;
            messenger = battleMessenger;
            P1.InputController.SelfDriven = false;
            P2.InputController.SelfDriven = false;
        }

        private void Update()
        {
            if (P1 == null || P2 == null) return;

            accumulator += Time.deltaTime;
            if (accumulator > MaxAccumulated) accumulator = MaxAccumulated;

            while (accumulator >= TickDelta)
            {
                accumulator -= TickDelta;
                Tick();
            }
        }

        private void Tick()
        {
            CurrentFrame++;

            // ① 朝向同步：位置关系决定朝向，写回角色与输入控制器（搓招镜像依赖它）
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

            // ④ 推挡解算：先把位置解算干净（防重叠、版边约束），
            //    再用干净的位置去做攻防判定——否则判定会读到穿模状态下的错误距离
            Pushbox.Resolve(P1, P2);

            // ⑤ 碰撞与攻防裁决（对称检测，支持相杀），结果发布到战斗消息总线
            Resolver.Resolve(CurrentFrame, P1, P2, hitEvents);
            for (int i = 0; i < hitEvents.Count; i++)
                messenger?.Publish(hitEvents[i]);

            // ⑥ 帧末广播
            TickFinished?.Invoke(CurrentFrame);
        }
    }
}