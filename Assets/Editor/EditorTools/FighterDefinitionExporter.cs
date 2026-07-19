using System.IO;
using Domain.Infrastructure.Battle;
using Domain.Net;
using Google.Protobuf;
using UnityEditor;
using UnityEngine;

namespace FTG.EditorTools
{
    /// <summary>
    /// 角色定义夹具导出（跨语言对拍的静态数据源）。
    /// 写到 server/testdata/frank_definition.pb（进 git）；Go 侧 sim/content.LoadDefinition 读它。
    ///
    /// 【何时重导】改过 FighterDefinition.cs 里的任何数值/条目/指令后。
    /// 忘了也没事：EditMode 守卫测试（FighterDefinitionFixtureTests）会红并提示。
    ///
    /// readText 传 null：导出的是【注入 JSON 前】的纯代码数据——判定框/位移
    /// 两端各自从 Assets/BoxData/*.json 装载，夹具不重复携带。
    /// （导出时 Console 出现两条"取不到 BoxData"警告属预期，可忽略。）
    /// </summary>
    public static class FighterDefinitionExporter
    {
        [MenuItem("FG/导出角色定义夹具（对拍）")]
        public static void ExportFrank()
        {
            var repo = new ExampleFighterDefinitionRepository(_ => null);
            FighterDefinition def = repo.Get("Frank");

            byte[] bytes = FighterDefinitionProtoCodec.ToProto(def).ToByteArray();

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "server", "testdata"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "frank_definition.pb");
            File.WriteAllBytes(path, bytes);

            Debug.Log($"[FighterDefinitionExporter] 已导出 {path}（{bytes.Length} 字节）。" +
                      "请提交进 git；Go 侧 go test ./... 将自动启用夹具装载测试。");
        }
    }
}
