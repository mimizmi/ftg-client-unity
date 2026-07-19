using System.IO;
using Domain.Infrastructure.Battle;
using Domain.Net;
using Google.Protobuf;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 角色定义夹具的防过期守卫：代码里的 Frank 定义序列化后必须与已提交的
    /// server/testdata/frank_definition.pb 逐字节一致。
    /// 你调过 FighterDefinition.cs 的数值而忘了重导夹具时，这条测试会红——
    /// 否则 Go 侧对拍会悄悄吃旧数据，把逻辑对拍的信号搞脏。
    /// </summary>
    public class FighterDefinitionFixtureTests
    {
        // EditMode 测试工作目录 = 工程根
        private const string FixturePath = "server/testdata/frank_definition.pb";

        private static byte[] CurrentBytes()
        {
            // readText=null：夹具存的是注入 JSON 前的纯代码数据（与导出器一致）
            var repo = new ExampleFighterDefinitionRepository(_ => null);
            return FighterDefinitionProtoCodec.ToProto(repo.Get("Frank")).ToByteArray();
        }

        [Test]
        public void Fixture_Exists_AndUpToDate()
        {
            Assert.That(File.Exists(FixturePath), Is.True,
                $"夹具不存在（{FixturePath}）：请运行菜单 FG/导出角色定义夹具（对拍） 并提交进 git。");

            byte[] committed = File.ReadAllBytes(FixturePath);
            byte[] current = CurrentBytes();

            Assert.That(committed, Is.EqualTo(current),
                "夹具过期：FighterDefinition.cs 的 Frank 定义改动后未重导。" +
                "请重新运行菜单 FG/导出角色定义夹具（对拍），否则 Go 侧对拍吃的是旧数据。");
        }

        [Test]
        public void Serialization_IsByteStable()
        {
            // 同一定义两次序列化字节一致——守卫比对成立的前提
            // （ReactionMoves 已按序数排序，其余字段按 proto 字段号序）
            Assert.That(CurrentBytes(), Is.EqualTo(CurrentBytes()));
        }
    }
}
