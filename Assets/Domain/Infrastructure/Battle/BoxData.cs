using System;
using System.Collections.Generic;
using Domain.Infrastructure.FixedPoint;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 判定框类型：
    /// - Hit:  攻击框，只在 Active 帧存在，碰到对方 Hurt 即命中
    /// - Hurt: 受击框，随动画姿态变化（蹲下变矮 → 中段打空、下段命中，这是机制的基础）
    /// - Push: 推挡框，防止两角色重叠，通常整个角色固定不变（那根"身体柱子"）
    /// </summary>
    public enum BoxKind : byte
    {
        Hit,
        Hurt,
        Push,
    }

    /// <summary>
    /// 判定框的一个关键帧。编辑器只在关键帧画框，中间帧线性插值补出——
    /// 一个 30 帧的招式画 3~4 个关键帧就够了，不必逐帧画 30 次。
    /// 【保持 float】这是 JSON 创作数据的 DTO（编辑器读写、JsonUtility 序列化），
    /// 格式不因定点化改变；运行时判定用的定点框由 BoxTrack 惰性烘焙。
    /// </summary>
    [Serializable]
    public sealed class BoxKeyframe
    {
        public int Frame;      // 招式内帧号（1 起始）
        public float X, Y, W, H;

        public Box ToBox() => new Box(X, Y, W, H); // float→Fix 的边界转换点

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
    /// 一个招式可以有多条同类轨道（比如横扫有两个 Hit 框覆盖不同区域）。
    /// 两条求值路径：
    ///   TryEvaluate          —— 运行时判定，全定点（关键帧惰性烘焙成 Fix，插值用 Fix）
    ///   TryEvaluateAuthoring —— 编辑器绘制/精调，浮点直读 Keys（数据可变，不缓存）
    /// </summary>
    [Serializable]
    public sealed class BoxTrack
    {
        public BoxKind Kind;
        public int FromFrame;   // 生效起始帧（含）
        public int ToFrame;     // 结束帧（含）
        public List<BoxKeyframe> Keys = new List<BoxKeyframe>();

        // 定点关键帧缓存（与 Keys 一一对应）。惰性烘焙：运行时数据加载后不再变化，
        // 首次判定转换一次终身使用；编辑器不走此路径，不存在缓存失效问题。
        [NonSerialized] private Box[] bakedKeys;

        public bool ActiveAt(int moveFrame) => moveFrame >= FromFrame && moveFrame <= ToFrame;

        /// <summary>求某帧的框（运行时判定路径，全定点）。关键帧间插值；越界钳到端点。</summary>
        public bool TryEvaluate(int moveFrame, out Box box)
        {
            box = default;
            if (!ActiveAt(moveFrame) || Keys.Count == 0) return false;

            if (bakedKeys == null || bakedKeys.Length != Keys.Count) Bake();

            if (Keys.Count == 1 || moveFrame <= Keys[0].Frame)
            {
                box = bakedKeys[0];
                return true;
            }

            int last = Keys.Count - 1;
            if (moveFrame >= Keys[last].Frame)
            {
                box = bakedKeys[last];
                return true;
            }

            for (int i = 0; i < last; i++)
            {
                BoxKeyframe a = Keys[i];
                BoxKeyframe b = Keys[i + 1];
                if (moveFrame < a.Frame || moveFrame > b.Frame) continue;

                int span = b.Frame - a.Frame;
                Fix t = span <= 0 ? Fix.Zero : Fix.FromFraction(moveFrame - a.Frame, span);
                box = Box.Lerp(bakedKeys[i], bakedKeys[i + 1], t);
                return true;
            }
            return false;
        }

        private void Bake()
        {
            bakedKeys = new Box[Keys.Count];
            for (int i = 0; i < Keys.Count; i++) bakedKeys[i] = Keys[i].ToBox();
        }

        /// <summary>
        /// 编辑器用的浮点插值（authoring 路径）：直读可变的 Keys，返回插值后的关键帧。
        /// 命中关键帧原点时返回的是【实例引用】——只读使用，改动请走编辑器的 UpsertKeyframe。
        /// </summary>
        public bool TryEvaluateAuthoring(int moveFrame, out BoxKeyframe key)
        {
            key = null;
            if (!ActiveAt(moveFrame) || Keys.Count == 0) return false;

            if (Keys.Count == 1 || moveFrame <= Keys[0].Frame)
            {
                key = Keys[0];
                return true;
            }

            BoxKeyframe last = Keys[Keys.Count - 1];
            if (moveFrame >= last.Frame)
            {
                key = last;
                return true;
            }

            for (int i = 0; i < Keys.Count - 1; i++)
            {
                BoxKeyframe a = Keys[i];
                BoxKeyframe b = Keys[i + 1];
                if (moveFrame < a.Frame || moveFrame > b.Frame) continue;

                float span = b.Frame - a.Frame;
                float t = span <= 0f ? 0f : (moveFrame - a.Frame) / span;
                key = BoxKeyframe.Lerp(a, b, t);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 一个招式的【手工创作数据】：帧分割、判定框、无敌帧。
    ///
    /// 这些都必须【看着动画】才定得准——脚第几帧离地、拳头第几帧伸出、伸到哪里。
    /// 凭空写不出来，所以必须有可视化工具（HitboxEditor），也必须存成可编辑的数据。
    ///
    /// 【位移不在这里】它在 {角色}_rootmotion.json。
    /// 位移是自动生成的（随时可从 clip 重烘），判定框是手工创作的（重画要几小时）。
    /// 混在一个文件里是反模式：一次误重烘可能连带毁掉手工数据，
    /// 且 git diff 被几百行数字淹没，判定框的改动根本看不出来。
    /// </summary>
    [Serializable]
    public sealed class MoveBoxData
    {
        public string MoveId;

        /// <summary>总帧数 = clip 帧数（60fps 采样下即 clip.length * 60）</summary>
        public int TotalFrames;

        // ---- 帧分割（三段之和应等于 TotalFrames）----
        // 攻击招式：可从 Hit 框的帧范围【一键推导】——你画完框，分割就有了
        // 移动招式：手动定，含义不同（起跳预备/落地缓冲、冲刺起步/收招）
        public int Startup;
        public int Active;
        public int Recovery;

        // ---- 无敌帧（升龙、后跃步）。0 = 无 ----
        public int InvulnFrom;
        public int InvulnTo;

        public List<BoxTrack> Tracks = new List<BoxTrack>();

        public bool HasFrameSplit => Startup + Active + Recovery > 0;
    }

    /// <summary>
    /// 一个角色的手工数据（对应 {角色}_boxes.json）。
    /// 这份文件值得 git review，diff 要看得清 —— 所以位移那几百行数字不放这儿。
    /// </summary>
    [Serializable]
    public sealed class CharacterBoxData
    {
        /// <summary>
        /// 格式版本。破坏性变更时递增，并在编辑器的 Migrate() 里写迁移逻辑。
        ///   1 = 初版：只有 Tracks
        ///   2 = 增加 Startup/Active/Recovery、InvulnFrom/To（纯增量，可直接读 v1）
        ///   3 = 移出 RootMotion（搬到独立的 rootmotion.json）
        /// 旧文件没有此字段 → 读出来是 0 → 视为 v1。
        /// </summary>
        public const int CurrentVersion = 3;

        public int Version = CurrentVersion;
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
