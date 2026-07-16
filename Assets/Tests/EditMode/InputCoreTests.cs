using Domain.Infrastructure;
using Domain.Infrastructure.Input;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>输入层基础件：环形缓冲、Numpad 记法、指令缓冲队列。</summary>
    public class InputCoreTests
    {
        // ---- InputBuffer 环形缓冲 ----

        [Test]
        public void InputBuffer_WrapsAround_KeepsNewest()
        {
            var buffer = new InputBuffer(capacity: 4);
            for (int i = 1; i <= 6; i++)
                buffer.Push(new InputFrame { Frame = i, Direction = (byte)(i % 10) });

            Assert.That(buffer.Count, Is.EqualTo(4));
            Assert.That(buffer.Latest.Frame, Is.EqualTo(6));

            Assert.That(buffer.TryGet(3, out InputFrame oldest), Is.True);
            Assert.That(oldest.Frame, Is.EqualTo(3), "容量 4 只保留最近 4 帧");
            Assert.That(buffer.TryGet(4, out _), Is.False, "越过保留范围必须报告失败而非脏数据");
        }

        [Test]
        public void InputBuffer_Clear_Empties()
        {
            var buffer = new InputBuffer(8);
            buffer.Push(new InputFrame { Frame = 1 });
            buffer.Clear();

            Assert.That(buffer.Count, Is.EqualTo(0));
            Assert.That(buffer.TryGet(0, out _), Is.False);
        }

        // ---- Numpad 记法 ----

        [Test]
        public void Numpad_FromAxes_MapsAllNine()
        {
            Assert.That(Numpad.FromAxes(-1, -1), Is.EqualTo(1));
            Assert.That(Numpad.FromAxes(0, -1), Is.EqualTo(2));
            Assert.That(Numpad.FromAxes(1, -1), Is.EqualTo(3));
            Assert.That(Numpad.FromAxes(-1, 0), Is.EqualTo(4));
            Assert.That(Numpad.FromAxes(0, 0), Is.EqualTo(5));
            Assert.That(Numpad.FromAxes(1, 0), Is.EqualTo(6));
            Assert.That(Numpad.FromAxes(-1, 1), Is.EqualTo(7));
            Assert.That(Numpad.FromAxes(0, 1), Is.EqualTo(8));
            Assert.That(Numpad.FromAxes(1, 1), Is.EqualTo(9));
        }

        [Test]
        public void Numpad_Mirror_SwapsHorizontals_KeepsVerticals()
        {
            Assert.That(Numpad.Mirror(1), Is.EqualTo(3));
            Assert.That(Numpad.Mirror(4), Is.EqualTo(6));
            Assert.That(Numpad.Mirror(7), Is.EqualTo(9));
            Assert.That(Numpad.Mirror(6), Is.EqualTo(4));
            Assert.That(Numpad.Mirror(2), Is.EqualTo(2));
            Assert.That(Numpad.Mirror(5), Is.EqualTo(5));
            Assert.That(Numpad.Mirror(8), Is.EqualTo(8));
        }

        // ---- CommandQueue 指令缓冲 ----

        [Test]
        public void CommandQueue_ExpiresAfterBufferFrames()
        {
            var queue = new CommandQueue { BufferFrames = 8 };
            queue.Enqueue("QCF_P", 100, currentFrame: 10); // ExpireFrame = 18

            queue.Tick(18);
            Assert.That(queue.Count, Is.EqualTo(1), "到期帧当帧仍有效（缓冲的宽容语义）");

            queue.Tick(19);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void CommandQueue_ReEnqueue_RefreshesExpiry_NoDuplicate()
        {
            var queue = new CommandQueue { BufferFrames = 8 };
            queue.Enqueue("QCF_P", 100, 10);
            queue.Enqueue("QCF_P", 100, 15); // 同名刷新而非追加

            Assert.That(queue.Count, Is.EqualTo(1));
            queue.Tick(20);
            Assert.That(queue.Count, Is.EqualTo(1), "过期时间应按第二次入队(15+8=23)计算");
        }

        [Test]
        public void CommandQueue_ConsumesBestPriority_ThenRemoves()
        {
            var queue = new CommandQueue { BufferFrames = 8 };
            queue.Enqueue("QCF_P", 100, 10);
            queue.Enqueue("DP_P", 110, 10);

            Assert.That(queue.TryConsume(out DetectedCommand best), Is.True);
            Assert.That(best.Id, Is.EqualTo("DP_P"), "歧义时高优先级指令先被消费");
            Assert.That(queue.Count, Is.EqualTo(1));

            Assert.That(queue.TryPeek(out DetectedCommand remaining), Is.True);
            Assert.That(remaining.Id, Is.EqualTo("QCF_P"));
        }

        [Test]
        public void CommandQueue_TryPeek_DoesNotRemove()
        {
            var queue = new CommandQueue { BufferFrames = 8 };
            queue.Enqueue("QCF_P", 100, 10);

            Assert.That(queue.TryPeek(out _), Is.True);
            Assert.That(queue.Count, Is.EqualTo(1));
        }
    }
}
