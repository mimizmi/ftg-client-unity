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
        
        [Header("Movement的 Animator State 名")]
        [SerializeField] private string walkForwardState = "WalkForward";
        [SerializeField] private string walkBackwardState = "WalkBackward";
        [SerializeField] private string dashState = "Dash";
        [SerializeField] private string runState = "Run";
        [SerializeField] private string backDashState = "BackDash";
        [SerializeField] private string jumpSquatState = "JumpSquat";
        [SerializeField] private string airborneState = "Airborne";
        [SerializeField] private string landingState = "Landing";
        
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
         private void SyncAnimation(FighterState _fighter)
        {
            switch (_fighter.Status)
            {
                case FighterStatus.Attacking:
                case FighterStatus.CounterStance:
                {
                    // 硬同步：把动画进度校准到逻辑帧。
                    // 前提：该 State 下动画长度 ≈ TotalFrames/60 秒；若美术给的动画
                    // 时长和帧数据不一致，这行会自动拉伸对齐——帧数据永远是权威。
                    MoveData move = _fighter.CurrentMove;
                    float normalized = (_fighter.MoveFrame - 1f) / move.TotalFrames;
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
                    PlayLoose(MovementStateName(_fighter.Movement.State));
                    break;
            }
        }
         
        /// <summary>
        /// 移动状态 → Animator State 名。这些也需要在 AC 里建同名 State。
        /// 移动动画不需要帧精确（不像招式），所以用 CrossFade 平滑过渡即可。
        /// </summary>
        private string MovementStateName(MovementState state)
        {
            switch (state)
            {
                case MovementState.WalkForward:  return walkForwardState;
                case MovementState.WalkBackward: return walkBackwardState;
                case MovementState.DashStartup:
                case MovementState.Dashing:      return dashState;
                case MovementState.Running:      return runState;
                case MovementState.DashRecovery: return idleState;
                case MovementState.BackDash:     return backDashState;
                case MovementState.JumpSquat:    return jumpSquatState;
                case MovementState.Airborne:     return airborneState;
                case MovementState.Landing:      return landingState;
                default:                         return idleState;
            }
        }
 
        /// <summary>非招式状态：不需要帧精确，用 CrossFade 平滑过渡，且避免每帧重入。</summary>
        private void PlayLoose(string stateName)
        {
            int hash = Animator.StringToHash(stateName);
            if (playingStateHash == hash) return;
            playingStateHash = hash;
            animator.CrossFade(hash, 0.08f);
        }
 
        private readonly Dictionary<string, int> moveStateHashCache = new Dictionary<string, int>();
 
        /// <summary>
        /// MoveId → Animator State 哈希，带缓存与契约校验。
        /// Animator.Play 对不存在的 State 会【静默失败】（画面停在上一个动作），
        /// 这种错极难排查——所以首次遇到某 MoveId 时用 HasState 验证一次，
        /// 名字对不上（拼写/大小写/放进了子状态机）立刻报出可定位的错误。
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