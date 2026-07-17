using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// Playables 动画驱动：绕过 Animator 状态机，用 PlayableGraph 直驱 clip。
    /// 为什么替换 animator.Play(hash, 0, normalized)：
    ///   ① 契约收窄：状态机要求「State 名 = MoveId」且必须待在 Base Layer 顶层，加招式要同步
    ///      维护 AC 连线；本图只要求「Clip 名 = MoveId」，AC 退化成 clip 清单（仅供采集）。
    ///   ② 时间权威干净：Manual 更新模式下播放头只在 Evaluate 时按我们给的时间走，
    ///      不再需要 speed=0 抑制自走、CrossFade 时又置回 1 的补丁对。
    ///   ③ 为将来留口：顿帧慢放、受击 clip 压缩、上下半身分层，都是 SetSpeed/权重/Mask 一行的事。
    /// 契约：clip 按【名字】从 Animator 的控制器里采集（Clip 名 = MoveId）；
    /// 构造后控制器被卸下——状态机与本图会争抢骨骼写入权，二选一。
    /// </summary>
    public sealed class FighterAnimationPlayer : IDisposable
    {
        private const float FadeSeconds = 0.08f; // 与旧 CrossFade 时长一致

        private PlayableGraph graph;
        private AnimationMixerPlayable mixer; // 口0 = 当前；口1 = 淡出中的上一个
        private readonly Dictionary<string, AnimationClip> clips =
            new Dictionary<string, AnimationClip>();
        private readonly Dictionary<string, AnimationClipPlayable> playables =
            new Dictionary<string, AnimationClipPlayable>();

        private string currentName;
        private string previousName;
        private float fadeRemaining;

        public FighterAnimationPlayer(Animator animator)
        {
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            if (controller != null)
            {
                foreach (AnimationClip clip in controller.animationClips)
                    if (clip != null && !clips.ContainsKey(clip.name))
                        clips[clip.name] = clip;
                animator.runtimeAnimatorController = null; // 图与状态机二选一
            }

            graph = PlayableGraph.Create($"{animator.gameObject.name}_FighterAnim");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); // 时间只听 Evaluate 的
            AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Anim", animator);
            mixer = AnimationMixerPlayable.Create(graph, 2);
            output.SetSourcePlayable(mixer);
            graph.Play();
        }

        public bool HasClip(string clipName) => clips.ContainsKey(clipName);

        /// <summary>硬同步：把 clip 播放头钉在 normalized（0~1）处并立即求值。逻辑帧是权威。</summary>
        public void SampleAt(string clipName, float normalized)
        {
            if (!SwitchTo(clipName, hardCut: true)) return;
            playables[clipName].SetTime(normalized * clips[clipName].length);
            graph.Evaluate(0f);
        }

        /// <summary>松散循环（待机类）：切换时从头播 + 短淡入，此后由 Tick 推进时间。</summary>
        public void PlayLooping(string clipName)
        {
            if (clipName == currentName) return;
            if (!SwitchTo(clipName, hardCut: false)) return;
            playables[clipName].SetTime(0);
        }

        /// <summary>松散模式的逐帧推进（真实时间；含交叉淡入权重动画）。硬同步姿势已钉住，不要调。</summary>
        public void Tick(float deltaTime)
        {
            if (fadeRemaining > 0f)
            {
                fadeRemaining = Mathf.Max(0f, fadeRemaining - deltaTime);
                float w = 1f - fadeRemaining / FadeSeconds;
                mixer.SetInputWeight(0, w);
                mixer.SetInputWeight(1, 1f - w);
                if (fadeRemaining <= 0f) DropPrevious();
            }
            graph.Evaluate(deltaTime);
        }

        /// <summary>接上新 clip。hardCut = 掐掉一切淡入尾巴（帧精确状态不容混合残影）。</summary>
        private bool SwitchTo(string clipName, bool hardCut)
        {
            if (clipName == currentName)
            {
                if (hardCut && previousName != null)
                {
                    DropPrevious(); // 淡入进行中突然转硬同步：立即收尾
                    fadeRemaining = 0f;
                    mixer.SetInputWeight(0, 1f);
                }
                return true;
            }

            if (!clips.ContainsKey(clipName)) return false;

            if (!playables.TryGetValue(clipName, out AnimationClipPlayable next))
            {
                next = AnimationClipPlayable.Create(graph, clips[clipName]);
                playables[clipName] = next;
            }

            string old = currentName;
            if (old != null) graph.Disconnect(mixer, 0);
            DropPrevious();

            currentName = clipName;
            graph.Connect(next, 0, mixer, 0);

            if (!hardCut && old != null && playables.TryGetValue(old, out AnimationClipPlayable prev))
            {
                // 上一姿势挂到口1 淡出（起始权重 1→0，由 Tick 推进）
                graph.Connect(prev, 0, mixer, 1);
                previousName = old;
                fadeRemaining = FadeSeconds;
                mixer.SetInputWeight(0, 0f);
                mixer.SetInputWeight(1, 1f);
            }
            else
            {
                fadeRemaining = 0f;
                mixer.SetInputWeight(0, 1f);
                mixer.SetInputWeight(1, 0f);
            }
            return true;
        }

        private void DropPrevious()
        {
            if (previousName == null) return;
            graph.Disconnect(mixer, 1);
            previousName = null;
        }

        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
