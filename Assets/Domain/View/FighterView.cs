using System.Collections.Generic;
using Domain.Infrastructure.Motion;
using Domain.Service;
using Domain.Service.Battle;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public class FighterView : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private string idleState = "Frank_FS4_Idle_Stand_Loop";
        [SerializeField] private string hitstunState = "Frank_FS4_Hit_High_Front";
        [SerializeField] private string blockstunState = "Frank_FS4_Hit_High_Front";

        [Header("朝向表现")]
        [Tooltip("RotateY: 3D 模型用（负缩放会翻转蒙皮法线）。MirrorScaleX: 2D 骨骼/精灵用")]
        [SerializeField] private FacingStyle facingStyle = FacingStyle.RotateY;
        [Tooltip("面朝右(+X)时的 Y 旋转。模型前向是局部 Z 时通常为 90")]
        [SerializeField] private float facingRightYaw = 90f;
        [Tooltip("面朝左(-X)时的 Y 旋转")]
        [SerializeField] private float facingLeftYaw = 270f;
        
        public enum FacingStyle : byte
        {
            RotateY,      // 3D 蒙皮模型：绕 Y 旋转
            MirrorScaleX, // 2D 骨骼/精灵：X 负缩放镜像
        }
 
        private BattleLoop loop;
        private FighterState fighter;

        /// <summary>当前绑定的逻辑侧角色（FX 层按它做 状态 → 视图 的映射）。未绑定时为 null。</summary>
        public FighterState Fighter => fighter;
 
        /// <summary>
        /// 由 BattleBootstrap 在实例化角色后调用，接上逻辑侧。
        /// 绑定前 LateUpdate 静默跳过（角色站在出生点等待开局是正常状态）。
        /// </summary>
        public void Bind(BattleLoop battleLoop, FighterState fighterState)
        {
            loop = battleLoop;
            fighter = fighterState;
 
            if (animator == null) animator = GetComponentInChildren<Animator>();
 
            // 运行时【必须关闭】：位置权威属于 FighterState，
            // Animator 再驱动一次位置就是双重位移（角色以两倍速度飞出去）。
            // 同样在代码里强制，不依赖 Prefab 勾选 —— 编辑期工具要开、运行期要关，
            // 靠人记这个规则迟早出事。
            if (animator != null && animator.applyRootMotion)
            {
                animator.applyRootMotion = false;
                Debug.LogWarning($"[FighterView] {name} 的 Apply Root Motion 已被强制关闭：" +
                                 "位置权威属于 FighterState，动画只是显示器。", this);
            }
        }
 
        private int lastSeenFrame = -1;
        private Vector2 prevPosition;    // 上一逻辑帧位置（插值起点）
        private Vector2 currentPosition; // 当前逻辑帧位置（插值终点）
        private int playingStateHash;    // 当前播放的非招式状态，避免重复 CrossFade
 
        private void LateUpdate()
        {
            if (fighter == null || loop == null) return;
 
            // ---- 逻辑帧推进检测：记录插值端点，并做一次动画校准 ----
            if (loop.CurrentFrame != lastSeenFrame)
            {
                int elapsed = loop.CurrentFrame - lastSeenFrame;
                // 正常推进 1 帧：旧终点变新起点；卡顿跨了多帧：直接跳，不做穿越插值
                prevPosition = (elapsed == 1 && lastSeenFrame >= 0) ? currentPosition : fighter.Position;
                currentPosition = fighter.Position;
                lastSeenFrame = loop.CurrentFrame;
 
                SyncAnimation(fighter);
            }
 
            // ---- 位置：两个逻辑帧之间按累加器进度插值 ----
            Vector2 rendered = Vector2.Lerp(prevPosition, currentPosition, loop.InterpolationAlpha);
            transform.position = new Vector3(rendered.x, rendered.y, transform.position.z);
 
            // ---- 朝向 ----
            if (facingStyle == FacingStyle.RotateY)
            {
                // 3D 蒙皮模型：绕 Y 旋转。你的模型前向是局部 Z、以 Y=90° 对齐战斗平面(+X)，
                // 面朝左即转到 270°。负缩放镜像会翻转蒙皮法线，3D 模型不可用
                transform.rotation = Quaternion.Euler(
                    0f, fighter.FacingRight ? facingRightYaw : facingLeftYaw, 0f);
            }
            else
            {
                // 2D 骨骼/精灵：X 负缩放镜像
                Vector3 scale = transform.localScale;
                scale.x = Mathf.Abs(scale.x) * (fighter.FacingRight ? 1f : -1f);
                transform.localScale = scale;
            }
        }
 
        private void SyncAnimation(FighterState fighter)
        {
            // 傀儡原则：播放头【完全】由逻辑帧驱动，Animator 自身不得自走时间。
            // speed=0 是关键——否则 Animator 会按真实时间前飘、再被每帧 Play 拽回，
            // 在 normalized 不变的场合（顿帧；受击 clip 播完但 hitstun 未结束的定格）
            // 来回抖 = 闪烁。唯一需要自走的是 PlayLoose 的 CrossFade，那里再临时置回 1。
            animator.speed = 0f;

            switch (fighter.Status)
            {
                case FighterStatus.Attacking:
                case FighterStatus.CounterStance:
                {
                    // 硬同步：把动画进度校准到逻辑帧。
                    // 前提：该 State 下动画长度 ≈ TotalFrames/60 秒；若美术给的动画
                    // 时长和帧数据不一致，这行会自动拉伸对齐——帧数据永远是权威。
                    MoveData move = fighter.CurrentMove;
                    float normalized = Mathf.Clamp01(fighter.MoveFrame / (float)move.TotalFrames);
                    animator.Play(GetMoveStateHash(move.MoveId), 0, normalized);
                    playingStateHash = 0;
                    break;
                }
 
                case FighterStatus.Hitstun:
                {
                    // 受击已是正式招式：播它的 clip，并用受击招式帧进度(ReactionFrame)硬同步——
                    // 与击退位移同一个驱动，表现与位移严丝合缝、不滑步（同招式/移动一套哲学）。
                    string reactionId = fighter.CurrentReactionMoveId;
                    if (!string.IsNullOrEmpty(reactionId) && fighter.ReactionTotalFrames > 0)
                    {
                        float n = Mathf.Clamp01(fighter.ReactionFrame / (float)fighter.ReactionTotalFrames);
                        animator.Play(ResolveState(reactionId, hitstunState), 0, n);
                        playingStateHash = 0;
                    }
                    else
                    {
                        // 无受击招式（类别未映射/None）→ 回退通用受击，按硬直窗口硬同步
                        PlayStunSynced(hitstunState, fighter);
                    }
                    break;
                }
 
                case FighterStatus.Blockstun:
                    PlayStunSynced(blockstunState, fighter);
                    break;
 
                default:
                    SyncMovementAnimation(fighter.Movement);
                    break;
            }
        }
 
        /// <summary>
        /// 移动动画。分两类处理：
        ///
        /// 【跳跃】美术给的是一整段完整动画（起跳预备→腾空→落地都在一个 clip 里），
        /// 按前/后/垂直分三个 clip。所以不能用 CrossFade 播完事——要把逻辑的三个阶段
        /// 映射到 clip 的归一化区间上，用 normalizedTime 驱动播放头：
        ///     clip:  [══起跳预备══|═══════腾空═══════|══落地══]
        ///            0        squatEnd            airEnd     1.0
        /// 这与招式动画的硬同步是同一个思路：逻辑是权威，动画是它的显示器。
        ///
        /// 【其余移动】走路/冲刺/后跃不需要帧精确，CrossFade 平滑过渡即可。
        /// </summary>
        private void SyncMovementAnimation(MovementController movement)
        {
            // 所有移动（走路/冲刺/后跃/跳跃/空中冲刺）都是招式，
            // 一律用 normalizedTime 由逻辑帧驱动播放头 —— 与招式动画完全同构。
            // 动画与位移严丝合缝：它们本就是同一份数据。
            string clipId = movement.MotionClipId;
            if (!string.IsNullOrEmpty(clipId))
            {
                int hash = ResolveState(clipId, idleState);
                animator.Play(hash, 0, movement.MotionNormalizedTime);
                playingStateHash = 0;
                return;
            }
 
            PlayLoose(idleState);
        }

        /// <summary>
        /// 硬直动画硬同步：用逻辑硬直进度 (总−剩余)/总 驱动 clip 播放头，
        /// 让受击/防御 clip 恰好铺满整段硬直窗口——与招式动画同构（逻辑帧是权威，
        /// clip 长于硬直则压缩、短于硬直则拉伸）。这样绝不会「没播完就被 idle 截断」。
        /// 想让受击看起来更久 → 调大该招的 HitstunFrames（同时也是连段/帧优势的平衡量）。
        /// </summary>
        private void PlayStunSynced(string stateName, FighterState fighter)
        {
            int hash = ResolveState(stateName, idleState);
            float t = fighter.StunTotalFrames > 0
                ? Mathf.Clamp01(1f - fighter.StunRemaining / (float)fighter.StunTotalFrames)
                : 0f;
            animator.Play(hash, 0, t);
            playingStateHash = 0; // 硬同步：清空重入保护，与招式/移动一致
        }

        /// <summary>非招式状态：不需要帧精确，用 CrossFade 平滑过渡，且避免每帧重入。</summary>
        private void PlayLoose(string stateName)
        {
            animator.speed = 1f; // CrossFade 过渡/循环需要 Animator 自走时间（覆盖 SyncAnimation 顶部的 0）
            int hash = ResolveState(stateName, idleState);
            if (playingStateHash == hash) return;
            playingStateHash = hash;
            animator.CrossFade(hash, 0.08f);
        }
 
        private readonly Dictionary<string, int> stateHashCache = new Dictionary<string, int>();
 
        /// <summary>
        /// State 名 → hash，带存在性校验与回退。
        /// Animator.Play/CrossFade 对不存在的 State 会【静默失败】（画面卡在上一个动作），
        /// 这种 bug 极难查——所以首次遇到时验证一次，缺失则回退到 fallback 并告警一次。
        /// </summary>
        private int ResolveState(string stateName, string fallback)
        {
            if (stateHashCache.TryGetValue(stateName, out int cached)) return cached;
 
            int hash = Animator.StringToHash(stateName);
            if (!animator.HasState(0, hash))
            {
                Debug.LogWarning(
                    $"[FighterView] Animator 缺少 State \"{stateName}\"，回退到 \"{fallback}\"。", this);
                hash = Animator.StringToHash(fallback);
            }
            stateHashCache[stateName] = hash;
            return hash;
        }
 
        private readonly Dictionary<string, int> moveStateHashCache = new Dictionary<string, int>();
 
        /// <summary>
        /// MoveId → Animator State 哈希。与移动状态不同，招式【不做回退】——
        /// 招式动画是帧数据的显示器，静默换成别的动画会让判定与画面严重脱节，
        /// 必须直接报错让人修 AC。
        /// </summary>
        private int GetMoveStateHash(string moveId)
        {
            if (moveStateHashCache.TryGetValue(moveId, out int hash))
                return hash;
 
            hash = Animator.StringToHash(moveId);
            if (!animator.HasState(0, hash))
            {
                Debug.LogError(
                    $"[FighterView] Animator 缺少招式对应的 State: \"{moveId}\"。" +
                    "检查：① State 名与 MoveId 完全一致（区分大小写）" +
                    "② State 位于 Base Layer 顶层（不在子状态机内）。" +
                    "可用菜单 FG/Animator Contract Validator 全量校验。", this);
            }
            moveStateHashCache[moveId] = hash;
            return hash;
        }
    }
}