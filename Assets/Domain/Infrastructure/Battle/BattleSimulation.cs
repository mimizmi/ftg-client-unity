using System;
using System.Collections.Generic;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>战斗宏观阶段。回合演出留白（Ready/Fight、KO 定格）也是模拟状态——进快照。</summary>
    public enum BattlePhase : byte
    {
        Intro,     // 回合开场留白（IntroFrames，0 = 直接开打）。双方冻结，输入照常采样
        Fighting,  // 交战中：完整攻防管线 + 计时
        RoundOver, // 回合结束定格（胜利/倒地演出期），双方冻结
        MatchOver, // 比赛结束，终态
    }

    /// <summary>回合规则参数。默认值 = 经典街霸（99 秒、三局两胜）。</summary>
    [Serializable]
    public sealed class BattleConfig
    {
        public int RoundFrames = 99 * 60;
        /// <summary>开场留白帧数。0 = 无留白（当前默认；接入 Ready/Fight 演出后由 UI 侧配置）。</summary>
        public int IntroFrames = 0;
        /// <summary>回合结束到下一回合（或比赛结束判定）的定格帧数。</summary>
        public int RoundOverFrames = 120;
        public int RoundsToWin = 2;
        public int MaxHealth = 1000;
    }

    /// <summary>一回合的判决。Winner：1/2 = 对应玩家，0 = 平（双 KO / 时间到血量相同，双方各记一胜）。</summary>
    public readonly struct RoundResult
    {
        public readonly int RoundNumber;
        public readonly int Winner;
        public readonly bool ByTimeout;

        public RoundResult(int roundNumber, int winner, bool byTimeout)
        {
            RoundNumber = roundNumber;
            Winner = winner;
            ByTimeout = byTimeout;
        }
    }

    /// <summary>
    /// 战斗模拟纯类：一场战斗的全部状态与每帧推进逻辑，零 MonoBehaviour、零渲染依赖。
    /// BattleLoop 只是它的时钟驱动器；EditMode 测试与将来的回滚重模拟直接驱动这里。
    ///
    /// 【帧序不可打乱】朝向 → 输入采样 → （按阶段）状态推进 → 推挡 → 攻防裁决 → 帧末广播。
    /// 推挡在攻防之前：先把位置解算干净（防重叠、版边约束），
    /// 再用干净的位置做判定——否则判定会读到穿模状态下的错误距离。
    ///
    /// 【回合系统】KO / 时间到 → RoundOver 定格 → 归位重开或 MatchOver。
    /// 冻结阶段（Intro/RoundOver/MatchOver）双方不推进，但输入照常采样——
    /// 输入流连续性是确定性重放的前提（回放文件不需要"跳过冻结帧"的特判）。
    /// </summary>
    public sealed class BattleSimulation
    {
        public FighterState P1 { get; }
        public FighterState P2 { get; }
        public CollisionResolver Resolver { get; }

        /// <summary>推挡解算：防重叠 + 版边约束。</summary>
        public PushboxResolver Pushbox { get; } = new PushboxResolver();

        public BattleConfig Config { get; }
        public int CurrentFrame { get; private set; }
        public BattlePhase Phase { get; private set; } = BattlePhase.Intro;
        public int RoundNumber { get; private set; } = 1;
        public int P1Wins { get; private set; }
        public int P2Wins { get; private set; }
        public int RoundFramesRemaining { get; private set; }

        /// <summary>HUD 计时器读数（向上取整：剩 1 帧也显示 1 秒）。</summary>
        public int RoundSecondsRemaining => (RoundFramesRemaining + 59) / 60;

        /// <summary>
        /// P1 打出的当前连段命中数（P2 为镜像语义）。规则：命中瞬间守方【已在】
        /// 受击硬直 → 计数 +1，否则重开为 1；守方脱离硬直且本帧无新命中 → 清零。
        /// 拼招/被投不计。这是模拟状态（确定性、进快照），HUD 只读显示。
        /// </summary>
        public int P1ComboHits { get; private set; }
        public int P2ComboHits { get; private set; }

        /// <summary>
        /// 每个命中/拼招事件（帧内顺序确定）。这是"核心 → 表现层"的单向出口：
        /// 订阅者（HUD/音效/演出）不得反向改战斗状态，否则破坏帧确定性。
        /// </summary>
        public event Action<HitEvent> HitOccurred;

        /// <summary>回合正式开打（Intro 结束）。参数 = 回合号（1 起）。</summary>
        public event Action<int> RoundStarted;

        /// <summary>回合判决出炉（进入 RoundOver 定格的那一帧）。</summary>
        public event Action<RoundResult> RoundEnded;

        /// <summary>比赛结束。参数 = 胜者（1/2，0 = 平局：双方同时拿满胜场）。</summary>
        public event Action<int> MatchEnded;

        /// <summary>
        /// 每逻辑帧末尾广播（冻结阶段也广播——它是"时间过了一帧"而非"打了一帧"）。
        /// 任何要【改动战斗状态】的逻辑（AI、假人自动反应、自动确反）只能挂这里。
        /// </summary>
        public event Action<int> TickFinished;

        private readonly List<HitEvent> hitEvents = new List<HitEvent>(4);
        private readonly Vector2 p1Spawn;
        private readonly Vector2 p2Spawn;
        private int phaseTimer;

        public BattleSimulation(FighterState p1, FighterState p2,
            CollisionResolver resolver, BattleConfig config = null)
        {
            P1 = p1;
            P2 = p2;
            Resolver = resolver;
            Config = config ?? new BattleConfig();

            // 出生点以组合根摆好的初始位置为准（回合重置回这里）
            p1Spawn = p1.Position;
            p2Spawn = p2.Position;

            RoundFramesRemaining = Config.RoundFrames;
            phaseTimer = Config.IntroFrames;
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

            // ② 同帧采样双方输入（内部完成搓招检测与指令入队）。
            //    冻结阶段也采样：输入流不许断，确定性重放不需要特判
            P1.InputController.ManualTick();
            P2.InputController.ManualTick();

            switch (Phase)
            {
                case BattlePhase.Intro:
                    // --phaseTimer：IntroFrames=0 时首帧即降为 -1 → 立刻开打，
                    // 与"没有回合系统"的旧行为逐帧一致
                    if (--phaseTimer <= 0)
                    {
                        Phase = BattlePhase.Fighting;
                        RoundStarted?.Invoke(RoundNumber);
                        TickFighting();
                    }
                    break;

                case BattlePhase.Fighting:
                    TickFighting();
                    break;

                case BattlePhase.RoundOver:
                    if (--phaseTimer <= 0) AdvanceRound();
                    break;

                case BattlePhase.MatchOver:
                    break; // 终态：等待外部（重赛/退出）处置
            }

            // ⑥ 帧末广播
            TickFinished?.Invoke(CurrentFrame);
        }

        private void TickFighting()
        {
            // ③ 双方状态推进（消费指令、招式帧 +1、移动状态机、硬直倒计时）
            P1.Tick(CurrentFrame);
            P2.Tick(CurrentFrame);

            // 连段判据采样点：必须在攻防裁决【之前】记录守方是否已在硬直——
            // Resolve 里 ApplyHit 会当场把守方置入新硬直，事后读不到"命中前"状态
            bool p1WasStunned = P1.Status == FighterStatus.Hitstun;
            bool p2WasStunned = P2.Status == FighterStatus.Hitstun;

            // ④ 推挡解算
            Pushbox.Resolve(P1, P2);

            // ⑤ 碰撞与攻防裁决（对称检测，支持相杀）
            Resolver.Resolve(CurrentFrame, P1, P2, hitEvents);
            bool hitOnP1 = false, hitOnP2 = false;
            for (int i = 0; i < hitEvents.Count; i++)
            {
                HitEvent ev = hitEvents[i];
                if (ev.Outcome == DefenseOutcome.Hit || ev.Outcome == DefenseOutcome.CounterHit)
                {
                    if (ev.Defender == P2) { P1ComboHits = p2WasStunned ? P1ComboHits + 1 : 1; hitOnP2 = true; }
                    else                   { P2ComboHits = p1WasStunned ? P2ComboHits + 1 : 1; hitOnP1 = true; }
                }
                HitOccurred?.Invoke(ev);
            }

            // 守方回到非硬直且本帧没挨新打 → 连段链断开
            if (!hitOnP2 && P2.Status != FighterStatus.Hitstun) P1ComboHits = 0;
            if (!hitOnP1 && P1.Status != FighterStatus.Hitstun) P2ComboHits = 0;

            RoundFramesRemaining--;
            CheckRoundEnd();
        }

        private void CheckRoundEnd()
        {
            bool p1Dead = P1.Health <= 0;
            bool p2Dead = P2.Health <= 0;
            bool timeout = RoundFramesRemaining <= 0;
            if (!p1Dead && !p2Dead && !timeout) return;

            int winner;
            if (p1Dead || p2Dead)
                winner = p1Dead && p2Dead ? 0 : p1Dead ? 2 : 1; // 双 KO = 平
            else
                winner = P1.Health == P2.Health ? 0 : P1.Health > P2.Health ? 1 : 2;

            // 平局双方各记一胜（经典规则：双 KO 双方都拿下这一回合）
            if (winner != 2) P1Wins++;
            if (winner != 1) P2Wins++;

            Phase = BattlePhase.RoundOver;
            phaseTimer = Config.RoundOverFrames;
            RoundEnded?.Invoke(new RoundResult(RoundNumber, winner, timeout));
        }

        private void AdvanceRound()
        {
            if (P1Wins >= Config.RoundsToWin || P2Wins >= Config.RoundsToWin)
            {
                Phase = BattlePhase.MatchOver;
                int matchWinner = P1Wins == P2Wins ? 0 : P1Wins > P2Wins ? 1 : 2;
                MatchEnded?.Invoke(matchWinner);
                return;
            }

            RoundNumber++;
            P1.ResetForRound(p1Spawn, Config.MaxHealth);
            P2.ResetForRound(p2Spawn, Config.MaxHealth);
            P1ComboHits = 0;
            P2ComboHits = 0;
            RoundFramesRemaining = Config.RoundFrames;
            phaseTimer = Config.IntroFrames;
            Phase = BattlePhase.Intro; // IntroFrames=0 → 下一帧直接开打
        }
    }
}
