using System.Collections.Generic;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Motion;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
   /// <summary>
    /// 招式数据的运行时加载器 —— 把两份 JSON 合并注入到 MoveData 上。
    ///
    /// 【数据来源的三分法】按"从哪来、谁负责、能不能重生成"划分：
    ///
    ///   ① {角色}_boxes.json      手工创作（HitboxEditor）
    ///      帧分割、判定框、无敌帧。看着动画一帧帧定的，重画要几小时。
    ///      值得 git review，diff 要看得清。
    ///
    ///   ② {角色}_rootmotion.json 自动生成（BatchRootMotionBaker）
    ///      每帧位移。随时可从 clip 重烘，机器产物，删了也不心疼。
    ///      几百行数字，不需要 review。
    ///
    ///   ③ FighterDefinition（代码）  纯设计数值
    ///      伤害、硬直、连招规则、攻击属性。与动画无关，手调手感时享受编译期检查。
    ///
    /// 【为什么 ① 和 ② 必须分文件】
    /// 把自动生成的数据和手工数据混在一个文件里是反模式：
    ///   · 一次误重烘可能连带毁掉手工数据
    ///   · git diff 被几百行数字淹没，判定框的改动根本看不出来
    ///   · 两者的变更频率、责任人、生命周期完全不同
    ///
    /// JSON 而非 ScriptableObject：Go 服务器要跑权威模拟，必须读同一份数据。
    /// </summary>
    public sealed class BoxDataLoader
    {
        // 数据 key（如 "BoxData/Frank_boxes"）→ JSON 文本；取不到返回 null。
        // 文本来源是注入的而非内置：运行时给 Addressables 读取器（帧数据可热更），
        // 测试给 File.ReadAllText——Core 不依赖任何资源系统（服务器侧可换读文件的同构实现）。
        private readonly System.Func<string, string> readText;

        public BoxDataLoader(System.Func<string, string> readText)
        {
            this.readText = readText;
        }

        private readonly Dictionary<string, CharacterBoxData> boxCache =
            new Dictionary<string, CharacterBoxData>();
 
        private readonly Dictionary<string, CharacterRootMotion> motionCache =
            new Dictionary<string, CharacterRootMotion>();
 
        /// <summary>加载并注入一个角色的全部动画派生数据。</summary>
        public void Apply(string characterId, MoveData[] moves)
        {
            CharacterBoxData boxes = LoadBoxes(characterId);
            CharacterRootMotion motion = LoadRootMotion(characterId);
 
            foreach (MoveData move in moves)
            {
                ApplyBoxes(boxes, move);
                ApplyRootMotion(motion, move);
            }
        }
 
        /// <summary>注入帧分割、判定框、无敌帧（来自手工编辑的 boxes.json）。</summary>
        private static void ApplyBoxes(CharacterBoxData boxes, MoveData move)
        {
            MoveBoxData data = boxes.Find(move.MoveId);
            if (data == null) return;
 
            move.BoxTracks = data.Tracks;
 
            // 只在 JSON 里真有值时才覆盖 —— 允许代码里先写占位值跑起来，
            // 等在 HitboxEditor 里定好后再由 JSON 接管
            if (data.HasFrameSplit)
            {
                move.Startup = data.Startup;
                move.Active = data.Active;
                move.Recovery = data.Recovery;
            }
 
            if (data.InvulnTo > 0)
            {
                move.InvulnFrom = data.InvulnFrom;
                move.InvulnTo = data.InvulnTo;
            }
        }
 
        /// <summary>
        /// 注入位移（来自自动烘焙的 rootmotion.json）。
        /// 取不到 = 原地招式，RootMotion 保持 null，角色不动 —— 语义正确，无需特判。
        /// </summary>
        private static void ApplyRootMotion(CharacterRootMotion motion, MoveData move)
        {
            MoveRootMotion rm = motion.Find(move.MoveId);
            if (rm?.Motion == null || rm.Motion.Length == 0) return;

            // 边界转换点：烘焙 JSON 是 float（格式不变、随时可重烘），
            // 装载时一次性转定点，此后模拟内全程整数运算（N2 定点化）
            var fixedMotion = new FixVec2[rm.Motion.Length];
            for (int i = 0; i < rm.Motion.Length; i++)
                fixedMotion[i] = FixVec2.FromFloat(rm.Motion[i].x, rm.Motion[i].y);
            move.RootMotion = fixedMotion;
 
            if (rm.Frames != move.TotalFrames)
            {
                Debug.LogWarning(
                    $"[BoxDataLoader] {move.MoveId} 的位移数据是 {rm.Frames} 帧，" +
                    $"但帧数据总长 {move.TotalFrames} 帧。动画改过？请重烘位移。");
            }
        }
 
        private CharacterBoxData LoadBoxes(string characterId)
        {
            if (boxCache.TryGetValue(characterId, out CharacterBoxData cached))
                return cached;
 
            string json = readText?.Invoke($"BoxData/{characterId}_boxes");
            CharacterBoxData data;

            if (json == null)
            {
                Debug.LogWarning(
                    $"[BoxDataLoader] 取不到 BoxData/{characterId}_boxes.json（未标记 Addressable 或文件缺失）。" +
                    "请用 FG/Hitbox Editor 编辑保存。角色将没有判定框（打不中也挨不着）。");
                data = new CharacterBoxData { CharacterId = characterId };
            }
            else
            {
                data = JsonUtility.FromJson<CharacterBoxData>(json)
                       ?? new CharacterBoxData { CharacterId = characterId };
            }
 
            boxCache[characterId] = data;
            return data;
        }
 
        private CharacterRootMotion LoadRootMotion(string characterId)
        {
            if (motionCache.TryGetValue(characterId, out CharacterRootMotion cached))
                return cached;
 
            string json = readText?.Invoke($"BoxData/{characterId}_rootmotion");
            CharacterRootMotion data;

            if (json == null)
            {
                Debug.LogWarning(
                    $"[BoxDataLoader] 取不到 BoxData/{characterId}_rootmotion.json（未标记 Addressable 或文件缺失）。" +
                    "请用 FG/Batch Root Motion Baker 烘焙。" +
                    "角色将【无法移动】——走路/冲刺/跳跃的位移全部来自这份数据。");
                data = new CharacterRootMotion { CharacterId = characterId };
            }
            else
            {
                data = JsonUtility.FromJson<CharacterRootMotion>(json)
                       ?? new CharacterRootMotion { CharacterId = characterId };
            }
 
            motionCache[characterId] = data;
            return data;
        }
    }
}