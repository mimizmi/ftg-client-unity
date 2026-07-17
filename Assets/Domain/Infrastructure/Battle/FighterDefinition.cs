using System;
using System.Collections.Generic;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
     public sealed class FighterDefinition
    {
        public string CharacterId;
 
        /// <summary>输入层：搓招指令的识别模式（MotionPattern.Id = 指令名）</summary>
        public MotionPattern[] Motions = Array.Empty<MotionPattern>();
 
        /// <summary>
        /// 招式表：指令/按键 + 姿态 + 取消来源 → 具体招式（MoveEntry.MoveId）。
        /// 连招规则（CancelFrom）配在这里。
        /// </summary>
        public MoveEntry[] MoveEntries = Array.Empty<MoveEntry>();
 
        /// <summary>战斗层：招式的帧数据（MoveData.MoveId = 招式名 = Animation Clip 名）</summary>
        public MoveData[] Moves = Array.Empty<MoveData>();
        
        public MovementConfig Movement = new MovementConfig();

        /// <summary>
        /// 受击类别 → 受击招式 MoveId。受击招式本身放在上面的 Moves 里（与其它动作一样
        /// 是带烘焙 RootMotion 的 MoveData），这里只做"类别→招式"的一层映射。
        /// 挨打方按自身姿态解析出类别后，据此取到受击招式并逐帧结算其击退位移。
        /// </summary>
        public Dictionary<HitReaction, string> ReactionMoves = new Dictionary<HitReaction, string>();
    }
 
    /// <summary>
    /// 角色定义仓库——游戏数据的唯一出处，以实例服务注册进 ApplicationContext。
    /// 这是对"静态类/场景脚本里硬编码数据"的纠正：数据所有权归仓库，
    /// 将来换成 ScriptableObject、Properties 配置文件或热更资源加载，
    /// 只需要换一个实现类，所有消费方（BattleBootstrap 等）一行不动。
    /// </summary>
    public interface IFighterDefinitionRepository
    {
        FighterDefinition Get(string characterId);
    }
 
    /// <summary>
    /// 示例实现：代码构造的角色数据（原 ExampleBattleSetup 中的硬编码搬到这里）。
    /// 定义会被缓存并在两名玩家间共享——这是安全的：MotionPattern / MoveData
    /// 在运行期是只读配置，可变的每次出招状态（如 moveConnected）存在 FighterState 里。
    /// </summary>
    public sealed class ExampleFighterDefinitionRepository : IFighterDefinitionRepository
    {
        private readonly Dictionary<string, FighterDefinition> cache =
            new Dictionary<string, FighterDefinition>();
 
        private readonly BoxDataLoader loader;

        /// <summary>
        /// readText：帧数据 JSON 的文本来源（key 如 "BoxData/Frank_boxes"，取不到返回 null）。
        /// 运行时传 Addressables 读取器（帧数据可热更），测试传 File.ReadAllText——
        /// 数据管道被注入，本仓库与 Core 均不再依赖 Resources。
        /// </summary>
        public ExampleFighterDefinitionRepository(Func<string, string> readText)
        {
            loader = new BoxDataLoader(readText);
        }
        
        public FighterDefinition Get(string characterId)
        {
            if (cache.TryGetValue(characterId, out FighterDefinition def))
                return def;
 
            def = Build(characterId);
 
            // 注入全部"从动画来的数据"：帧分割、判定框、无敌帧、位移
            loader.Apply(characterId, def.Moves);
 
            cache[characterId] = def;
            return def;
        }
 
        private FighterDefinition Build(string characterId)
        {
            switch (characterId)
            {
                case "Frank": return BuildShoto();
                default:
                    throw new ArgumentException($"未知角色: {characterId}");
            }
        }
 
        private FighterDefinition BuildShoto()
        {
            ButtonMask anyPunch = ButtonMask.LP | ButtonMask.MP | ButtonMask.HP;
            ButtonMask anyKick = ButtonMask.LK | ButtonMask.MK | ButtonMask.HK;
 
            return new FighterDefinition
            {
                CharacterId = "Frank",
 
                // 搓招指令表。注意 MotionPattern.Id 是【指令名】，不是招式名——
                // 236P 这一个指令，下面的招式表会按 LP/MP/HP 解析成三个不同招式。
                Motions = new[]
                {
                    // 623P = 升龙指令（前→下→前下）。注意这里只是【识别】了指令，
                    // 还需在 MoveEntries 配 CommandId="623P" 的条目 + 对应 MoveData + Animator clip，
                    // 升龙才真正能出（目前②③④尚缺，故 623P 暂时搓出来也不产生招）。
                    MotionLibrary.Dp("623P", anyPunch),
                    MotionLibrary.DashForward(),          // 指令名 "DASH_F"
                    MotionLibrary.DashBackward(),         // 指令名 "DASH_B"
                },
 
                // ===== 招式表：指令/按键 → 具体招式。连招和动画名都在这里定 =====
                // MoveId 同时就是 MoveData 的键、Animator State 名、Animation Clip 名。
                MoveEntries = new[]
                {
                    // --- 普通技：同一按键，姿态决定招式（站/蹲各一套动画）---
                    // CancelFrom（后摇通道）：命中/拼中后可从列出的招取消出 → gatling 连段。
                    // FeintFrom（前摇通道）：可在列出招式的【前摇】期间无需命中直接切出 → 变招/试探招。
                    // 这里让 LP↔LK 互为两条通道的来源：
                    //   打中了 → 取消续招（连招）；还没打出去 → 变招改主意（虚实）。
                    // 注：目前是交替无限 chain，Phase 1 会加"每招在一段连里只能用一次"的上限。
                    // 轻攻击取消网的统一规则：【拳从脚接，脚从拳接，站蹲随意换】。
                    // CancelFrom（命中后连招）与 FeintFrom（前摇变招）都按这条规则配——
                    // 好记，且站蹲切换自然融入连段（5LP 命中 → 按住下+LK 直接接 2LK）。
                    new MoveEntry { Buttons = ButtonMask.LP, Stance = Stance.Standing, MoveId = "Frank_FS4_Attack_Punch_L_02", Priority = 10,
                        CancelFrom = new[] { "Frank_FS4_Attack_Kick_L_02", "Frank_FS4_Attack_Crouch_Kick_01" },
                        FeintFrom  = new[] { "Frank_FS4_Attack_Kick_L_02", "Frank_FS4_Attack_Crouch_Kick_01" } },
                    new MoveEntry { Buttons = ButtonMask.LK, Stance = Stance.Standing, MoveId = "Frank_FS4_Attack_Kick_L_02", Priority = 10,
                        CancelFrom = new[] { "Frank_FS4_Attack_Punch_L_02", "Frank_FS4_Attack_Crouch_Punch_01" },
                        FeintFrom  = new[] { "Frank_FS4_Attack_Punch_L_02", "Frank_FS4_Attack_Crouch_Punch_01" } },
                    // 轻拳目标连：LP 连点 Punch_02→03→04→05。每段从【前一记拳】取消出（同键目标连的关键）。
                    // CancelOnly=true 使 03/04/05 只在连里存在、中立态点 LP 永远是起手的 Punch_02。
                    new MoveEntry { Buttons = ButtonMask.LP, Stance = Stance.Standing, MoveId = "Frank_FS4_Attack_Punch_L_03", Priority = 20,
                        CancelFrom = new[] { "Frank_FS4_Attack_Punch_L_02" }, CancelOnly = true},
                    new MoveEntry { Buttons = ButtonMask.LP, Stance = Stance.Standing, MoveId = "Frank_FS4_Attack_Punch_L_04", Priority = 20,
                        CancelFrom = new[] { "Frank_FS4_Attack_Punch_L_03" }, CancelOnly = true},
                    new MoveEntry { Buttons = ButtonMask.LP, Stance = Stance.Standing, MoveId = "Frank_FS4_Attack_Punch_L_05", Priority = 20,
                        CancelFrom = new[] { "Frank_FS4_Attack_Punch_L_04" }, CancelOnly = true},

                    // --- 蹲姿技（2LP / 2LK）---
                    // 姿态由方向键决定（1/2/3=蹲），同一按键在蹲姿解析到这两条。
                    // 取消/变招同样走"拳从脚接，脚从拳接"：站蹲四个轻攻击织成一张互通的网。
                    new MoveEntry { Buttons = anyPunch, Stance = Stance.Crouching, MoveId = "Frank_FS4_Attack_Crouch_Punch_01", Priority = 10,
                        CancelFrom = new[] { "Frank_FS4_Attack_Kick_L_02", "Frank_FS4_Attack_Crouch_Kick_01" },
                        FeintFrom  = new[] { "Frank_FS4_Attack_Kick_L_02", "Frank_FS4_Attack_Crouch_Kick_01" } },
                    new MoveEntry { Buttons = anyKick, Stance = Stance.Crouching, MoveId = "Frank_FS4_Attack_Crouch_Kick_01", Priority = 10,
                        CancelFrom = new[] { "Frank_FS4_Attack_Punch_L_02", "Frank_FS4_Attack_Crouch_Punch_01" },
                        FeintFrom  = new[] { "Frank_FS4_Attack_Punch_L_02", "Frank_FS4_Attack_Crouch_Punch_01" } },
                },
 
                Moves = new[]
                {
                    // 普通拳：发生5 / 持续3 / 恢复8，判定框第 6~8 帧生效
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Punch_L_02",
                        Startup = 5, Active = 3, Recovery = 10, 
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 50,
                        CancelFrom = 0,
                        Reaction = HitReaction.StandLight, 
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Punch_L_03",
                        Startup = 5, Active = 3, Recovery = 10, 
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 50,
                        CancelFrom = 5, 
                        Reaction = HitReaction.StandLight, 
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Punch_L_04",
                        Startup = 5, Active = 3, Recovery = 10, 
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 50,
                        CancelFrom = 5, 
                        Reaction = HitReaction.StandLight, 
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Punch_L_05",
                        Startup = 5, Active = 3, Recovery = 10,
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 50, HitstunFrames = 50,
                        CancelFrom = 5, 
                        Reaction = HitReaction.StandLight, 
                    },
                    new MoveData
                    {
                        // 粘贴后可按手感手调；帧数据是权威，动画服从它。
                        MoveId = "Frank_FS4_Attack_Kick_L_02",
                        Startup = 6, Active = 3, Recovery = 12, // 现实轻脚帧数（原 16/4/34 太长太僵）
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 25,
                        CancelFrom = 5, // 命中后从判定帧起可取消 → 连招
                        Reaction = HitReaction.StandMedium, // 轻脚 → 中受击（蹲姿命中降为 CrouchHeavy）
                    },

                    // ===== 蹲姿轻攻击。帧数先对标站立轻攻击，按 clip 实际长度和手感再调 =====
                    // ⚠ 出招前必须用 FG/Hitbox Editor 画 Hit/Hurt 框存进 boxes.json——
                    // 没有 Hit 框这两招【打不中】（判定层直接跳过）；位移用 Root Motion Baker 烘。
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Crouch_Punch_01",
                        Startup = 5, Active = 3, Recovery = 10,
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 50,
                        CancelFrom = 5,
                        Reaction = HitReaction.CrouchLight, // 挨打方按自身姿态自动降档（蹲→CrouchLight）
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Crouch_Kick_01",
                        Startup = 6, Active = 3, Recovery = 12,
                        // Low = 下段语义。防御已移除，但该属性保留：将来立回/AI/受击分档都会用它
                        Attributes = AttackAttribute.Strike | AttackAttribute.Low,
                        Damage = 30, HitstunFrames = 25,
                        CancelFrom = 5,
                        Reaction = HitReaction.CrouchLight,
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Idle_Stand_Loop",
                        Startup = 0, Active = 60, Recovery = 0, // 全程可行动；总帧数 = 待机 clip 帧数
                        Attributes = AttackAttribute.None,
                    },

                    // 蹲姿待机：循环、零位移。下蹲的机制价值全在【判定框】——
                    // 用 FG/Hitbox Editor 给它画一套比站立【矮】的 Hurt/Push 框存进 boxes.json，
                    // "蹲下躲上段"才真正成立（画之前回退站立 Idle 的框 = 蹲了不变矮，只是姿态判定变了）。
                    // Animator 缺同名 State 时表现层回退站立待机并告警，逻辑不受影响。
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Idle_Crouch_Loop",
                        Startup = 0, Active = 60, Recovery = 0, // 占位帧数，须 = 蹲姿 clip 帧数
                        Attributes = AttackAttribute.None,
                    },

                    // 下蹲/起身过渡（三段式：Crouching → Idle_Crouch_Loop → Standing）。
                    // 一次性动画，零位移，全程可出招（过渡纯视觉）。中途改主意会在两段过渡间
                    // 按【对称进度】接续（MovementController.MirrorProgress），所以两段的
                    // TotalFrames 都必须 = 各自 clip 的真实帧数，否则接续点会错位。
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Crouching", // 站→蹲
                        Startup = 0, Active = 10, Recovery = 0, // 占位帧数，须 = clip 帧数
                        Attributes = AttackAttribute.None,
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Standing", // 蹲→站
                        Startup = 0, Active = 10, Recovery = 0, // 占位帧数，须 = clip 帧数
                        Attributes = AttackAttribute.None,
                    },
                     new MoveData
                    {
                        MoveId = "Frank_FS4_8Way_QuickWalk_F",
                        Startup = 0, Active = 32, Recovery = 0, // 全程可行动；总帧数 = clip 帧数
                        Attributes = AttackAttribute.None,
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_8Way_QuickWalk_B",
                        Startup = 0, Active = 32, Recovery = 0,
                        Attributes = AttackAttribute.None
                    },
 
                    // 冲刺：一次性动画。Startup=起步(锁死,被抓就是确反)
                    // Active=推进(可取消出招 dash cancel) / Recovery=收招(锁死,不能防)
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Dash_Forward",
                        Startup = 3, Active = 12, Recovery = 4,
                        Attributes = AttackAttribute.None,
                    },
 
                    // 后跃：无敌帧写在 InvulnFrom/To 里 —— 这是后跃能"逃"的原因
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Dash_Backward",
                        Startup = 0, Active = 18, Recovery = 4,
                        Attributes = AttackAttribute.None,
                        InvulnFrom = 1, InvulnTo = 7,
                    },
 
                    // 跳跃：Startup=起跳预备(仍在地面,被抓到就是确反)
                    // Active=腾空(可出空中招) / Recovery=落地硬直
                    // 抛物线烘在动画里，不需要 velocity/gravity 另算一条
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Jump_N_High_All",
                        Startup = 5, Active = 38, Recovery = 4, // 总帧数须等于 clip 帧数
                        Attributes = AttackAttribute.None
                    },

                    // ===== 受击招式：与其它动作同构，带烘焙位移（击退）=====
                    // 硬直期间由 FighterState 逐帧结算其 RootMotion。TotalFrames 应 = 受击 clip 帧数；
                    // 位移用 FG/Batch Root Motion Baker 烘进 rootmotion.json（前进轴向后=击退方向）。
                    // 先给一条示例，映射多个类别到同一 clip；有了各档受击 clip 再拆开。
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Hit_High_Front",
                        Startup = 0, Active = 20, Recovery = 0, // Active 占位，按实际受击 clip 帧数调
                        Attributes = AttackAttribute.None,
                    },
                    
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Hit_Mid_Front",
                        Startup = 0, Active = 20, Recovery = 0, // Active 占位，按实际受击 clip 帧数调
                        Attributes = AttackAttribute.None,
                    },
                    // 前跳/后跳：与垂直跳同构（前上=9 触发前跳，后上=7 触发后跳，见 MovementController）。
                    // 位移(抛物线+水平前/后)由 FG/Batch Root Motion Baker 烘进 rootmotion.json，
                    // 运行时按 MoveId 注入——所以这里【不写】RootMotion。
                    // ⚠ MoveId 须与三处一致：config.JumpForwardId/BackwardId、Animator State 名、
                    // rootmotion.json 的 key。有真 clip 后把这三处一起改成真名（如 Frank_FS4_Jump_F_High_All）。
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Jump_F_High_All",
                        Startup = 5, Active = 38, Recovery = 4, // 占位帧数，须 = 前跳 clip 帧数
                        Attributes = AttackAttribute.None,
                    },
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Jump_B_High_All",
                        Startup = 5, Active = 38, Recovery = 4, // 占位帧数，须 = 后跳 clip 帧数
                        Attributes = AttackAttribute.None,
                    },
                },

                // 受击类别 → 受击招式。示例先全部指向同一条受击 clip；
                // 有了站/蹲/浮空各档的受击动画后，把对应类别改到各自的 MoveId 即可。
                ReactionMoves = new Dictionary<HitReaction, string>
                {
                    { HitReaction.StandLight,  "Frank_FS4_Hit_High_Front" },
                    { HitReaction.StandMedium, "Frank_FS4_Hit_Mid_Front" },
                    { HitReaction.StandHeavy,  "Frank_FS4_Hit_High_Front" },
                    { HitReaction.CrouchLight, "Frank_FS4_Defence_Mid_Crouch_F" },
                    { HitReaction.CrouchHeavy, "Frank_FS4_Defence_Mid_Crouch_F" },
                    { HitReaction.AirHit,      "Frank_FS4_Hit_High_Front" },
                },
            };
        }
    }
}