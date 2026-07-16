using System;
using System.Collections.Generic;
using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Battle
{
    public enum Stance : byte
    {
        Standing,
        Crouching,
        Airborne,
    }

    /// <summary>
    /// 取消通道：解析出招时处于哪种取消语境。取消分两条通道，规则完全不同：
    /// - OnHit（后摇通道）：当前招已命中/拼中，从判定帧起可取消续招 → 连招。空挥不可。
    /// - Feint（前摇通道）：当前招仍在 Startup（未 Active），无需命中即切换成【不同】的招
    ///   → 武术的变招/试探招。能变成什么由 MoveEntry.FeintFrom 数据决定。
    /// </summary>
    public enum CancelKind : byte
    {
        None,   // 中立态出招（不取消任何招）
        OnHit,  // 命中取消（后摇通道）
        Feint,  // 变招（前摇通道）
    }
     /// <summary>
    /// 招式表条目：把"输入层的指令"翻译成"战斗层的具体招式"。
    ///
    /// 这里是两个 ID 的交汇点，务必分清：
    /// - CommandId  = MotionPattern.Id，输入层的【指令名】（玩家的手做了什么）
    /// - MoveId     = MoveData.MoveId，战斗层的【招式名】（角色出了什么招），
    ///                它同时也是 Animator State / Animation Clip 的名字。
    /// 两者是多对多：同一指令按不同键/不同姿态/不同气槽 → 不同招式；
    /// 同一招式也可由多个指令触发。
    /// </summary>
    public sealed class MoveEntry
    {
        /// <summary>输入指令名（对应 MotionPattern.Id）。普通技用按键触发时可留空。</summary>
        public string CommandId;
 
        /// <summary>触发按键。搓招指令通常也要限定具体键（236+LP 与 236+HP 是不同招）。</summary>
        public ButtonMask Buttons = ButtonMask.None;
 
        /// <summary>目标招式（对应 MoveData.MoveId，也是 Animation Clip / Animator State 名）。</summary>
        public string MoveId;
 
        /// <summary>允许的姿态。</summary>
        public Stance Stance = Stance.Standing;
 
        /// <summary>
        /// 命中取消来源（后摇通道）：本招可以从哪些招式【命中/拼中后】直接取消出招（连招的核心规则）。
        /// null/空 = 只能在中立态出。非空 = 既可中立出，也可从列表中的招取消出。
        /// 例：236P 的 CancelFrom = ["5LP", "2LP"] → 轻拳命中后可取消接波动。
        /// </summary>
        public string[] CancelFrom;

        /// <summary>
        /// 变招来源（前摇通道）：本招可以在哪些招式的【前摇（Startup）】期间直接切出，
        /// 【无需命中】——武术的变招/试探招。与 CancelFrom 互不相干：
        /// CancelFrom 管"打中之后接什么"（连招），FeintFrom 管"还没打出去时改主意"（虚实）。
        /// 变招必须"变"：解析时强制目标 ≠ 来源招，原招不能自我重启（否则无限白拉前摇）。
        /// </summary>
        public string[] FeintFrom;
 
        /// <summary>优先级：同一输入匹配到多条时取高者（超必 > 必杀 > 普通技）。</summary>
        public int Priority;

        /// <summary>
        /// 仅可作为取消/连段中出现，不能从中立态直接出。
        /// 目标连的中段/收招（如 5LP→5LP→升拳 里那记"升拳"专属段）设 true，
        /// 使它只在连里存在、不污染中立态招式池。默认 false = 中立态也可出（普通技/gatling 目标）。
        /// </summary>
        public bool CancelOnly;
 
        /// <summary>额外条件（气槽、血量等）。null = 无条件。</summary>
        public Func<FighterState, bool> Condition;
    }
 
    /// <summary>
    /// 角色招式表——指令到招式的解析器，连招（chain / cancel）规则的唯一落点。
    ///
    /// 它取代了原先 FighterState.TryAct 里"指令名 == 招式名"的 1:1 硬编码假设。
    /// 解析优先级：Priority 降序，首个满足全部条件（姿态、按键、取消窗口、自定义条件）的条目胜出。
    /// </summary>
    public sealed class MoveTable
    {
        private readonly List<MoveEntry> entries = new List<MoveEntry>();
 
        public void Add(MoveEntry entry)
        {
            entries.Add(entry);
            entries.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
 
        public void AddRange(IEnumerable<MoveEntry> range)
        {
            foreach (MoveEntry e in range) entries.Add(e);
            entries.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
 
        /// <summary>
        /// 用搓招指令解析招式。cancelSource 为 null（kind=None）表示中立态出招；
        /// 非 null 表示正在取消——cancelKind 决定走哪条通道（命中取消 / 变招）。
        /// </summary>
        public string ResolveCommand(string commandId, ButtonMask pressed,
            Stance stance, string cancelSource, CancelKind cancelKind, FighterState fighter)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                MoveEntry e = entries[i];
                if (e.CommandId != commandId) continue;
                if (!Match(e, pressed, stance, cancelSource, cancelKind, fighter)) continue;
                return e.MoveId;
            }
            return null;
        }

        /// <summary>用裸按键解析普通技（无 CommandId 的条目）。</summary>
        public string ResolveButton(ButtonMask pressed, Stance stance,
            string cancelSource, CancelKind cancelKind, FighterState fighter)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                MoveEntry e = entries[i];
                if (!string.IsNullOrEmpty(e.CommandId)) continue;
                if (!Match(e, pressed, stance, cancelSource, cancelKind, fighter)) continue;
                return e.MoveId;
            }
            return null;
        }

        private static bool Match(MoveEntry e, ButtonMask pressed, Stance stance,
            string cancelSource, CancelKind cancelKind, FighterState fighter)
        {
            if (e.Stance != stance) return false;
            if (e.Buttons != ButtonMask.None && (pressed & e.Buttons) == 0) return false;

            switch (cancelKind)
            {
                case CancelKind.None:
                    // 中立态出招：CancelFrom/FeintFrom 不限制中立出招；
                    // 但 CancelOnly 的招（目标连专属段）不许从中立出
                    if (e.CancelOnly) return false;
                    break;

                case CancelKind.OnHit:
                    // 命中取消（后摇通道）：来源招必须在本条目的取消来源列表里
                    if (e.CancelFrom == null || Array.IndexOf(e.CancelFrom, cancelSource) < 0)
                        return false;
                    break;

                case CancelKind.Feint:
                    // 变招（前摇通道）：来源招必须在变招来源列表里，且必须"变"成不同招
                    if (e.FeintFrom == null || Array.IndexOf(e.FeintFrom, cancelSource) < 0)
                        return false;
                    if (e.MoveId == cancelSource) return false;
                    break;
            }

            return e.Condition == null || e.Condition(fighter);
        }
    }
}