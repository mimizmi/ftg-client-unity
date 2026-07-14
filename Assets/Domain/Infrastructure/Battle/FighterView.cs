using System;
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
 
        /// <summary>
        /// 由 BattleBootstrap 在实例化角色后调用，接上逻辑侧。
        /// 绑定前 LateUpdate 静默跳过（角色站在出生点等待开局是正常状态）。
        /// </summary>
        public void Bind(BattleLoop battleLoop, FighterState fighterState)
        {
            loop = battleLoop;
            fighter = fighterState;
 
            if (animator == null)
                animator = GetComponentInChildren<Animator>();
 
            // 位置权威在逻辑侧，Animator 驱动位置会与之打架
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
            switch (fighter.Status)
            {
                case FighterStatus.Attacking:
                case FighterStatus.CounterStance:
                {
                    // 硬同步：把动画进度校准到逻辑帧。
                    // 前提：该 State 下动画长度 ≈ TotalFrames/60 秒；若美术给的动画
                    // 时长和帧数据不一致，这行会自动拉伸对齐——帧数据永远是权威。
                    MoveData move = fighter.CurrentMove;
                    float normalized = (fighter.MoveFrame - 1f) / move.TotalFrames;
                    animator.Play(GetMoveStateHash(move.MoveId), 0, normalized);
                    playingStateHash = 0;
                    break;
                }
 
                case FighterStatus.Hitstun:
                    PlayLoose(hitstunState);
                    break;
 
                case FighterStatus.Blockstun:
                    PlayLoose(blockstunState);
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
            // 于是动画与位移严丝合缝：它们本就是同一份数据。
            string clipId = movement.MotionClipId;
            if (!string.IsNullOrEmpty(clipId))
            {
                int hash = ResolveState(clipId, idleState);
                animator.Play(hash, 0, movement.MotionNormalizedTime);
                playingStateHash = 0; // 让下一个 CrossFade 状态能正常进入
                return;
            }
 
            // 无移动招式 = 站立
            PlayLoose(idleState);
        }
 
        /// <summary>非招式状态：不需要帧精确，用 CrossFade 平滑过渡，且避免每帧重入。</summary>
        private void PlayLoose(string stateName)
        {
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