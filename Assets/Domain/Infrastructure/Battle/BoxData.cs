using System;
using System.Collections.Generic;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 判定框类型。三者的语义与生命周期都不同：
    /// - Hit:  攻击框，只在招式的 Active 帧存在，碰到对方 Hurt 即命中
    /// - Hurt: 受击框，几乎每帧都存在，随动画姿态变化（蹲下变矮 → 上段打空、下段命中）
    /// - Push: 推挡框，防止两角色重叠，通常整招固定不变
    /// </summary>
    public enum BoxKind : byte
    {
        Hit,
        Hurt,
        Push,
    }

    /// <summary>
    /// 判定框的一个关键帧。编辑器只在关键帧上画框，中间帧由系统线性插值补出——
    /// 一个 30 帧的招式，手画 3~4 个关键帧就够了，不用逐帧画 30 次。
    /// </summary>
    [Serializable]
    public sealed class BoxKeyframe
    {
        public int Frame;      // 招式内帧号（1 起始）
        public float X, Y, W, H;

        public Box ToBox() => new Box(X, Y, W, H);

        public static BoxKeyframe Lerp(BoxKeyframe a, BoxKeyframe b, float t) => new BoxKeyframe
        {
            X = Mathf.Lerp(a.X, b.X, t),
            Y = Mathf.Lerp(a.Y, b.Y, t),
            W = Mathf.Lerp(a.W, b.W, t),
            H = Mathf.Lerp(a.H, b.H, t),
        };
    }

    /// <summary>
    /// 一条判定框轨道：一个框在整个招式期间的生命史。
    /// 招式可以有多条同类轨道（比如一记横扫有两个 Hit 框覆盖不同区域）。
    ///
    /// 数据形态说明：用【关键帧 + 插值】而非逐帧硬编码，
    /// 因为编辑器里手画 30 帧不现实，而拳头的轨迹本来就是连续的。
    /// 运行时求值一次插值，成本可忽略。
    /// </summary>
    [Serializable]
    public sealed class BoxTrack
    {
        public BoxKind Kind;
        public int FromFrame;   // 轨道生效的起始帧（含）
        public int ToFrame;     // 结束帧（含）
        public List<BoxKeyframe> Keys = new List<BoxKeyframe>();

        public bool ActiveAt(int moveFrame) => moveFrame >= FromFrame && moveFrame <= ToFrame;

        /// <summary>
        /// 求某帧的框。关键帧之间线性插值；帧号在首/末关键帧之外则钳制到端点。
        /// </summary>
        public bool TryEvaluate(int moveFrame, out Box box)
        {
            box = default;
            if (!ActiveAt(moveFrame) || Keys.Count == 0) return false;

            if (Keys.Count == 1 || moveFrame <= Keys[0].Frame)
            {
                box = Keys[0].ToBox();
                return true;
            }

            BoxKeyframe last = Keys[Keys.Count - 1];
            if (moveFrame >= last.Frame)
            {
                box = last.ToBox();
                return true;
            }

            for (int i = 0; i < Keys.Count - 1; i++)
            {
                BoxKeyframe a = Keys[i];
                BoxKeyframe b = Keys[i + 1];
                if (moveFrame < a.Frame || moveFrame > b.Frame) continue;

                float span = b.Frame - a.Frame;
                float t = span <= 0f ? 0f : (moveFrame - a.Frame) / span;
                box = BoxKeyframe.Lerp(a, b, t).ToBox();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 一个招式的全部判定框数据（JSON 可序列化）。
    ///
    /// 【为什么直接用 JSON、跳过 ScriptableObject】
    /// 服务器要用 Go 跑权威模拟，必须读同一份帧数据。SO 是 Unity 私有序列化格式，
    /// Go 读不了——迁到 SO 只是一段注定要拆掉的脚手架。JSON 是跨语言中立格式，
    /// 编辑器读写方便，Unity 和 Go 各自加载同一个文件，一份真相。
    /// </summary>
    [Serializable]
    public sealed class MoveBoxData
    {
        public string MoveId;
        public int TotalFrames;
        public List<BoxTrack> Tracks = new List<BoxTrack>();
    }

    /// <summary>一个角色的全部招式判定框（对应一个 JSON 文件）。</summary>
    [Serializable]
    public sealed class CharacterBoxData
    {
        public string CharacterId;
        public List<MoveBoxData> Moves = new List<MoveBoxData>();

        public MoveBoxData Find(string moveId)
        {
            for (int i = 0; i < Moves.Count; i++)
                if (Moves[i].MoveId == moveId) return Moves[i];
            return null;
        }
    }
}