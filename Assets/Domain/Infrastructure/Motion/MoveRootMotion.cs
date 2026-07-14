using System;
using System.Collections.Generic;
using UnityEngine;

namespace Domain.Infrastructure.Motion
{
    /// <summary>
    /// 一个招式的根位移数据（从动画烘焙，60Hz）。
    ///
    /// 【帧语义 —— 必须和运行时/编辑器三方一致，否则判定框会差一帧的位移】
    ///   Motion[i] = pose((i+1)/60) - pose(i/60)        ← 第 i+1 帧【期间】的位移
    ///
    /// 于是逻辑第 F 帧（1 起始）：
    ///   · 位置 = 起点 + Motion[0..F-1] 之和 = pose(F/60) - pose(0)
    ///   · 动画 = pose(F/60)，即 normalizedTime = F / TotalFrames
    ///   · 判定框坐标相对上面那个位置（逻辑原点）
    /// 三者对齐，画面、位移、判定框严丝合缝。
    /// </summary>
    [Serializable]
    public sealed class MoveRootMotion
    {
        public string MoveId;
        public int Frames;
 
        /// <summary>每帧位移增量（面朝右空间：X=前后, Y=上下）</summary>
        public Vector2[] Motion;
 
        public Vector2 At(int frame)
        {
            if (Motion == null) return Vector2.zero;
            int i = frame - 1;
            return (i >= 0 && i < Motion.Length) ? Motion[i] : Vector2.zero;
        }
 
        /// <summary>逻辑第 frame 帧的累计位移（= 该帧的逻辑原点相对起点的偏移）。</summary>
        public Vector2 AccumulatedTo(int frame)
        {
            if (Motion == null) return Vector2.zero;
            Vector2 sum = Vector2.zero;
            int n = Mathf.Min(frame, Motion.Length);
            for (int i = 0; i < n; i++) sum += Motion[i];
            return sum;
        }
    }
 
    /// <summary>
    /// 一个角色的全部根位移（对应 {角色}_rootmotion.json）。
    ///
    /// 【为什么与判定框分成两份 JSON】
    /// 位移是【自动生成】的——随时可以从 clip 重烘，机器产物。
    /// 判定框/帧分割是【手工创作】的——重画要几小时，人的劳动。
    ///
    /// 把自动生成与手工数据混在一个文件里是反模式：
    ///   · 一次误操作的重烘可能连带毁掉手工数据
    ///   · git diff 被几百行数字淹没，判定框的改动根本看不出来
    ///   · 两者的生命周期、责任人、变更频率都不同
    ///
    /// 分开之后：rootmotion.json 可以随时删掉重生成，boxes.json 值得仔细 review。
    /// </summary>
    [Serializable]
    public sealed class CharacterRootMotion
    {
        public const int CurrentVersion = 1;
 
        public int Version = CurrentVersion;
        public string CharacterId;
 
        /// <summary>烘焙时用的前进轴，记录下来便于排查（3D 角色通常是 Z）。</summary>
        public string ForwardAxis = "Z";
 
        public List<MoveRootMotion> Moves = new List<MoveRootMotion>();
 
        public MoveRootMotion Find(string moveId)
        {
            for (int i = 0; i < Moves.Count; i++)
                if (Moves[i].MoveId == moveId) return Moves[i];
            return null;
        }
    }
}