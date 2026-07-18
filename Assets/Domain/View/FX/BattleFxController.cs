using System.Collections.Generic;
using Domain.Infrastructure.Battle;
using Domain.Service.Battle;
using UnityEngine;

namespace Domain.View.FX
{
    /// <summary>
    /// 打击感总控：订阅模拟事件（HitOccurred / RoundStarted / RoundEnded），
    /// 把命中翻译成 闪白 + 火花 + 震屏，把 KO 翻译成溶解。
    /// 【只读观察者】——与 HUD 同级，绝不反向写模拟。
    /// 战斗随流程反复开/停（Simulation 会整个换掉），这里在 Update 轮询变化自动重绑，
    /// autoStart 裸战斗与 GameFlow 两种模式都不需要任何接线。
    /// </summary>
    public sealed class BattleFxController : MonoBehaviour
    {
        [SerializeField] private BattleLoop loop;
        [SerializeField] private CameraShaker shaker;       // 可选：不拖就没有震屏
        [SerializeField] private HitSparkPool sparkPool;    // 可选：不拖就没有火花

        // 受击【不】闪白——对标街霸6：普通命中的反馈交给火花粒子 + 震屏 + 受击动画，
        // 闪材质只保留给"拼招"这种需要机制辨识度的瞬间（金闪 = 双方攻击相遇抵消）。
        [Header("拼招金闪")]
        [SerializeField] private float flashSeconds = 0.07f; // ≈4 帧
        [SerializeField] private Color clashFlashColor = new Color(1f, 0.85f, 0.3f);

        [Header("震屏强度（trauma 0~1）")]
        [SerializeField] private float hitShake = 0.25f;
        [SerializeField] private float counterHitShake = 0.4f;
        [SerializeField] private float clashShake = 0.45f;

        private BattleSimulation bound;
        private readonly Dictionary<FighterState, RendererFlasher> flashers =
            new Dictionary<FighterState, RendererFlasher>();

        private void Update()
        {
            if (loop != null && loop.Simulation != bound)
                Rebind(loop.Simulation);
        }

        private void Rebind(BattleSimulation sim)
        {
            if (bound != null)
            {
                bound.HitOccurred -= OnHit;
                bound.RoundStarted -= OnRoundStarted;
                bound.RoundEnded -= OnRoundEnded;
            }

            bound = sim;
            flashers.Clear();
            if (bound == null) return;

            bound.HitOccurred += OnHit;
            bound.RoundStarted += OnRoundStarted;
            bound.RoundEnded += OnRoundEnded;

            // FighterState → 视图根 的映射：角色实例由 Bootstrap 刚刚建好
            foreach (FighterView view in FindObjectsOfType<FighterView>())
            {
                if (view.Fighter == null) continue;
                Transform root = view.transform.root;
                var flasher = root.GetComponent<RendererFlasher>();
                if (flasher == null) flasher = root.gameObject.AddComponent<RendererFlasher>();
                flashers[view.Fighter] = flasher;
            }
        }

        private void OnHit(HitEvent ev)
        {
            switch (ev.Outcome)
            {
                case DefenseOutcome.Hit:
                case DefenseOutcome.CounterHit:
                {
                    shaker?.Shake(ev.Outcome == DefenseOutcome.CounterHit ? counterHitShake : hitShake);
                    sparkPool?.Play(ContactPoint(ev));
                    break;
                }
                case DefenseOutcome.Clashed:
                {
                    // 拼招：双方攻击相遇互相抵消，两边都闪金 + 更重的震屏
                    if (flashers.TryGetValue(ev.Attacker, out RendererFlasher a)) a.Flash(flashSeconds, clashFlashColor);
                    if (flashers.TryGetValue(ev.Defender, out RendererFlasher d)) d.Flash(flashSeconds, clashFlashColor);
                    shaker?.Shake(clashShake);
                    sparkPool?.Play(ContactPoint(ev));
                    break;
                }
            }
        }

        private static Vector3 ContactPoint(HitEvent ev)
        {
            // 判定层算好的真实接触点（攻击框∩受击框交集中心），蹲踢在腿、跳踢在头，不再估算
            return new Vector3(ev.ContactPoint.X.ToFloat(), ev.ContactPoint.Y.ToFloat(), 0f);
        }

        private void OnRoundStarted(int round)
        {
            foreach (RendererFlasher flasher in flashers.Values)
                if (flasher != null) flasher.ResetVisual();
        }

        private void OnRoundEnded(RoundResult result)
        {
            // 败者溶解；平局（双 KO / 同血量超时）双双消散
            float duration = bound.Config.RoundOverFrames / 60f * 0.8f; // 定格期内溶完，留点余韵
            if (result.Winner != 2 && flashers.TryGetValue(bound.P2, out RendererFlasher p2))
                p2.StartDissolve(duration);
            if (result.Winner != 1 && flashers.TryGetValue(bound.P1, out RendererFlasher p1))
                p1.StartDissolve(duration);
        }

        private void OnDestroy()
        {
            if (bound == null) return;
            bound.HitOccurred -= OnHit;
            bound.RoundStarted -= OnRoundStarted;
            bound.RoundEnded -= OnRoundEnded;
        }
    }
}
