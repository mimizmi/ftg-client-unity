using System.Collections.Generic;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    /// <summary>
    /// 判定框数据的运行时加载器。
    ///
    /// 数据流：HitboxEditor 可视化编辑 → JSON 文件 → 本类加载 → 注入 MoveData
    ///                                        ↘ 将来的 Go 服务器读同一份
    ///
    /// JSON 放在 Resources/BoxData/{characterId}_boxes.json。
    /// 上服务器时把加载路径换成正式的资源系统即可，调用方不受影响。
    /// </summary>
    public sealed class BoxDataLoader
    {
        private readonly Dictionary<string, CharacterBoxData> cache =
            new Dictionary<string, CharacterBoxData>();

        public CharacterBoxData Load(string characterId)
        {
            if (cache.TryGetValue(characterId, out CharacterBoxData data))
                return data;

            var asset = Resources.Load<TextAsset>($"BoxData/{characterId}_boxes");
            if (asset == null)
            {
                Debug.LogWarning(
                    $"[BoxDataLoader] 找不到判定框数据: Resources/BoxData/{characterId}_boxes.json。" +
                    "请用 FG/Hitbox Editor 编辑并保存到该路径。");
                data = new CharacterBoxData { CharacterId = characterId };
            }
            else
            {
                data = JsonUtility.FromJson<CharacterBoxData>(asset.text);
            }

            cache[characterId] = data;
            return data;
        }

        /// <summary>把 JSON 里的判定框数据注入到招式帧数据上。</summary>
        public void Apply(CharacterBoxData boxData, MoveData[] moves)
        {
            foreach (MoveData move in moves)
            {
                MoveBoxData data = boxData.Find(move.MoveId);
                if (data == null) continue;
 
                move.BoxTracks = data.Tracks;
 
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
 
                if (data.RootMotion != null && data.RootMotion.Length > 0)
                    move.RootMotion = data.RootMotion;
            }
        }
    }
}