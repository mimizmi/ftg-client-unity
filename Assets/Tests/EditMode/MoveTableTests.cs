using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 招式表解析规则：双通道取消（OnHit 连招 / Feint 变招）、CancelOnly、姿态过滤、优先级。
    /// 这里钉住的是本作的核心设计："打中之后接什么"与"没打出去时改主意"是两条互不相干的通道。
    /// </summary>
    public class MoveTableTests
    {
        // ---- 中立态（CancelKind.None）----

        [Test]
        public void Neutral_ResolvesPlainNormal()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "5LP", Priority = 10 });

            string moveId = table.ResolveButton(ButtonMask.LP, Stance.Standing,
                cancelSource: null, CancelKind.None, fighter: null);

            Assert.That(moveId, Is.EqualTo("5LP"));
        }

        [Test]
        public void Neutral_RejectsCancelOnlyEntry_EvenWithHigherPriority()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "5LP", Priority = 10 });
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LP, MoveId = "TC_2", Priority = 20,
                CancelOnly = true, CancelFrom = new[] { "5LP" },
            });

            string moveId = table.ResolveButton(ButtonMask.LP, Stance.Standing,
                null, CancelKind.None, null);

            Assert.That(moveId, Is.EqualTo("5LP"), "目标连专属段不得污染中立态招式池");
        }

        [Test]
        public void Neutral_WrongButton_ResolvesNothing()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "5LP", Priority = 10 });

            Assert.That(table.ResolveButton(ButtonMask.HK, Stance.Standing,
                null, CancelKind.None, null), Is.Null);
        }

        // ---- 命中取消通道（CancelKind.OnHit）----

        [Test]
        public void OnHit_SourceInCancelFrom_Resolves()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LP, MoveId = "TC_2", Priority = 20,
                CancelOnly = true, CancelFrom = new[] { "5LP" },
            });

            string moveId = table.ResolveButton(ButtonMask.LP, Stance.Standing,
                cancelSource: "5LP", CancelKind.OnHit, null);

            Assert.That(moveId, Is.EqualTo("TC_2"));
        }

        [Test]
        public void OnHit_SourceNotInCancelFrom_ResolvesNothing()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LP, MoveId = "TC_2", Priority = 20,
                CancelOnly = true, CancelFrom = new[] { "5LP" },
            });

            Assert.That(table.ResolveButton(ButtonMask.LP, Stance.Standing,
                "2LK", CancelKind.OnHit, null), Is.Null);
        }

        // ---- 变招通道（CancelKind.Feint）----

        [Test]
        public void Feint_SourceInFeintFrom_ResolvesDifferentMove()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LK, MoveId = "5LK", Priority = 10,
                FeintFrom = new[] { "5LP" },
            });

            string moveId = table.ResolveButton(ButtonMask.LK, Stance.Standing,
                cancelSource: "5LP", CancelKind.Feint, null);

            Assert.That(moveId, Is.EqualTo("5LK"));
        }

        [Test]
        public void Feint_MustChangeMove_SelfRestartRejected()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LP, MoveId = "5LP", Priority = 10,
                FeintFrom = new[] { "5LP" }, // 配置成"能从自己变出"也必须被拒
            });

            Assert.That(table.ResolveButton(ButtonMask.LP, Stance.Standing,
                "5LP", CancelKind.Feint, null), Is.Null,
                "变招必须【变】：原招自我重启会无限白拉前摇");
        }

        [Test]
        public void Feint_DoesNotUseCancelFromChannel()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LK, MoveId = "5LK", Priority = 10,
                CancelFrom = new[] { "5LP" }, // 只配了后摇通道
            });

            Assert.That(table.ResolveButton(ButtonMask.LK, Stance.Standing,
                "5LP", CancelKind.Feint, null), Is.Null,
                "两条通道互不相干：CancelFrom 不得放行前摇变招");
        }

        // ---- 姿态与指令 ----

        [Test]
        public void Stance_CrouchEntry_OnlyResolvesWhileCrouching()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "5LP", Priority = 10 });
            table.Add(new MoveEntry
            {
                Buttons = ButtonMask.LP, MoveId = "2LP", Priority = 10,
                Stance = Stance.Crouching,
            });

            Assert.That(table.ResolveButton(ButtonMask.LP, Stance.Standing,
                null, CancelKind.None, null), Is.EqualTo("5LP"));
            Assert.That(table.ResolveButton(ButtonMask.LP, Stance.Crouching,
                null, CancelKind.None, null), Is.EqualTo("2LP"));
        }

        [Test]
        public void Command_ResolvesByCommandIdAndButton()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry
            {
                CommandId = "QCF_P", Buttons = ButtonMask.LP,
                MoveId = "Fireball_L", Priority = 100,
            });

            Assert.That(table.ResolveCommand("QCF_P", ButtonMask.LP, Stance.Standing,
                null, CancelKind.None, null), Is.EqualTo("Fireball_L"));
            Assert.That(table.ResolveCommand("QCF_P", ButtonMask.HK, Stance.Standing,
                null, CancelKind.None, null), Is.Null, "指令还需按键匹配（236+LP ≠ 236+HK）");
            Assert.That(table.ResolveCommand("DP_P", ButtonMask.LP, Stance.Standing,
                null, CancelKind.None, null), Is.Null);
        }

        [Test]
        public void Priority_HigherWins_WhenBothMatch()
        {
            var table = new MoveTable();
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "Low", Priority = 1 });
            table.Add(new MoveEntry { Buttons = ButtonMask.LP, MoveId = "High", Priority = 99 });

            Assert.That(table.ResolveButton(ButtonMask.LP, Stance.Standing,
                null, CancelKind.None, null), Is.EqualTo("High"));
        }
    }
}
