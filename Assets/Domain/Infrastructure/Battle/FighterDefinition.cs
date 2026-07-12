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
 
        public FighterDefinition Get(string characterId)
        {
            if (cache.TryGetValue(characterId, out FighterDefinition def))
                return def;
 
            def = Build(characterId);
            cache[characterId] = def;
            return def;
        }
 
        private FighterDefinition Build(string characterId)
        {
            switch (characterId)
            {
                case "EXAMPLE_SHOTO": return BuildShoto();
                default:
                    throw new ArgumentException($"未知角色: {characterId}");
            }
        }
 
        private FighterDefinition BuildShoto()
        {
            ButtonMask anyPunch = ButtonMask.LP | ButtonMask.MP | ButtonMask.HP;
 
            return new FighterDefinition
            {
                CharacterId = "EXAMPLE_SHOTO",
 
                // 搓招指令表。注意 MotionPattern.Id 是【指令名】，不是招式名——
                // 236P 这一个指令，下面的招式表会按 LP/MP/HP 解析成三个不同招式。
                Motions = new[]
                {
                    MotionLibrary.LP("623P", anyPunch)
                },
 
                // ===== 招式表：指令/按键 → 具体招式。连招和动画名都在这里定 =====
                // MoveId 同时就是 MoveData 的键、Animator State 名、Animation Clip 名。
                MoveEntries = new[]
                {
                    // --- 普通技：同一按键，姿态决定招式（站/蹲各一套动画）---
                    new MoveEntry { Buttons = ButtonMask.LP, Stance = Stance.Standing,  MoveId = "Frank_FS4_Attack_Punch_L_02", Priority = 0 },
                },
 
                Moves = new[]
                {
                    // 普通拳：发生5 / 持续3 / 恢复8，判定框第 6~8 帧生效
                    new MoveData
                    {
                        MoveId = "Frank_FS4_Attack_Punch_L_02",
                        Startup = 10, Active = 2, Recovery = 28,
                        Attributes = AttackAttribute.Strike | AttackAttribute.Mid,
                        Damage = 30, HitstunFrames = 11, BlockstunFrames = 8,
                        RootMotion = MoveData.MotionFromSpans(40, (3,10, new Vector2(0.25f/7, 0f))),
                        CancelFrom = 5, // 命中后从判定帧起可取消 → 连招
                        Hitboxes = new[]
                        {
                            new BoxSpan { FromFrame = 10, ToFrame = 12, Box = new Box(0.55f, 1.1f, 0.7f, 0.3f) },
                        },
                    },
                },
            };
        }
    }
}