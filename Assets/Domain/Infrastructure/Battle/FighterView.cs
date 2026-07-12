using System;
using System.Collections.Generic;
using Domain.Infrastructure.Motion;
using Domain.Service;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public class FighterView : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] FightingInputController inputController;
        [SerializeField] private string idleState = "Frank_FS4_Idle_Stand_Loop";
        [SerializeField] private string hitstunState = "Frank_FS4_Hit_High_Front";
        [SerializeField] private string blockstunState = "Frank_FS4_Hit_High_Front";
        
        private int lastSeenFrame = -1;
        private Vector2 prevPosition;    // 上一逻辑帧位置（插值起点）
        private Vector2 currentPosition; // 当前逻辑帧位置（插值终点）
        private int playingStateHash;    // 当前播放的非招式状态，避免重复 CrossFade
        private FighterState fighter;
        private IFighterDefinitionRepository fighterDefinitionRepository;

        private void Start()
        {
            fighterDefinitionRepository = new ExampleFighterDefinitionRepository();
            fighter = BuildPlayer("EXAMPLE_SHOTO", inputController, fighterDefinitionRepository.Get("EXAMPLE_SHOTO"), new Vector2(-1.0f, -.2f));
            inputController.Ticked += fighter.Tick;
        }

        private void OnDestroy()
        {
            inputController.Ticked -= fighter.Tick;
        }

        private void LateUpdate()
        {
            if (inputController.CurrentFrame != lastSeenFrame)
            {
                int elapsed = inputController.CurrentFrame - lastSeenFrame;
                // 正常推进 1 帧：旧终点变新起点；卡顿跨了多帧：直接跳，不做穿越插值
                prevPosition = (elapsed == 1 && lastSeenFrame >= 0) ? currentPosition : fighter.Position;
                currentPosition = fighter.Position;
                lastSeenFrame = inputController.CurrentFrame;
 
                SyncAnimation(fighter);
            }
            
            Vector2 rendered = Vector2.Lerp(prevPosition, currentPosition, inputController.InterpolationAlpha);
            transform.position = new Vector3(rendered.x, rendered.y, transform.position.z);
 
            // ---- 朝向：镜像缩放（2D 骨骼/精灵通用；3D 模型可改为绕 Y 轴旋转 180°）----
            Vector3 scale = transform.localScale;
            scale.x = Mathf.Abs(scale.x) * (fighter.FacingRight ? 1f : -1f);
            transform.localScale = scale;
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
                    PlayLoose(idleState);
                    break;
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
        
        private FighterState BuildPlayer( string name,
            FightingInputController input, FighterDefinition definition,
            Vector2 position)
        {
            // 用仓库数据装配检测器与角色（定义是共享只读配置，可安全用于双方）
            foreach (MotionPattern motion in definition.Motions)
                input.Detector.Add(motion);
 
            // 招式表：指令 → 招式的解析层（连招规则在这里）
            var moveTable = new MoveTable();
            moveTable.AddRange(definition.MoveEntries);
 
            var fighter = new FighterState(input, moveTable) { Name = name, Position = position };
            foreach (MoveData move in definition.Moves)
                fighter.AddMove(move);
            
            return fighter;
        }
    }
}