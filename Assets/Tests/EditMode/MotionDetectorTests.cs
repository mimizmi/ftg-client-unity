using System.Collections.Generic;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using NUnit.Framework;

namespace FTG.Tests
{
    /// <summary>
    /// 搓招识别表：确定性输入序列 → 确定的指令识别结果。
    /// 这些测试同时是输入层的"帧级契约"——回滚网络重放输入时必须得到完全相同的识别。
    /// </summary>
    public class MotionDetectorTests
    {
        private InputBuffer buffer;
        private MotionDetector detector;
        private readonly List<MotionPattern> results = new List<MotionPattern>(4);
        private int frame;

        [SetUp]
        public void SetUp()
        {
            buffer = new InputBuffer(120);
            detector = new MotionDetector();
            frame = 0;
        }

        /// <summary>压入一帧输入（方向 + 本帧刚按下的键）。</summary>
        private void Push(byte dir, ButtonMask pressed = ButtonMask.None)
        {
            buffer.Push(new InputFrame
            {
                Frame = ++frame,
                Direction = dir,
                Held = pressed,
                Pressed = pressed,
                Released = ButtonMask.None,
            });
        }

        private List<MotionPattern> Detect(bool facingRight = true)
        {
            detector.DetectAll(buffer, facingRight, results);
            return results;
        }

        // ---- 波动 236 ----

        [Test]
        public void Qcf_CleanSequence_IsDetected()
        {
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));

            Push(5); Push(5);
            Push(2); Push(3); Push(6, ButtonMask.LP);

            Assert.That(Detect(), Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("QCF_P"));
        }

        [Test]
        public void Qcf_WithSlack_InsideWindow_IsDetected()
        {
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));

            // 每步之间磨蹭几帧（< MaxGap 8，总长 < TotalWindow 22）：宽容度设计的验证
            Push(2); Push(2); Push(2);
            Push(3); Push(3); Push(3);
            Push(6); Push(6, ButtonMask.LP);

            Assert.That(Detect(), Has.Count.EqualTo(1));
        }

        [Test]
        public void Qcf_ExceedsTotalWindow_IsRejected()
        {
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));

            Push(2);
            for (int i = 0; i < 22; i++) Push(5); // 2 被推到窗口(22帧)之外
            Push(3); Push(6, ButtonMask.LP);

            Assert.That(Detect(), Is.Empty);
        }

        [Test]
        public void Qcf_ButtonWithoutMotion_IsRejected()
        {
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));

            Push(5); Push(5); Push(5, ButtonMask.LP);

            Assert.That(Detect(), Is.Empty);
        }

        [Test]
        public void Qcf_FacingLeft_UsesMirroredDirections()
        {
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));

            // 面朝左时，世界方向 2,1,4 = 逻辑方向 2,3,6
            Push(5); Push(2); Push(1); Push(4, ButtonMask.LP);

            Assert.That(Detect(facingRight: false), Has.Count.EqualTo(1));
            Assert.That(Detect(facingRight: true), Is.Empty, "镜像语义：面朝右时同一世界输入不该识别");
        }

        // ---- 升龙 623 与歧义裁决 ----

        [Test]
        public void Dp_CleanSequence_IsDetected()
        {
            detector.Add(MotionLibrary.Dp("DP_P", ButtonMask.LP));

            Push(5); Push(6); Push(2); Push(3, ButtonMask.LP);

            Assert.That(Detect(), Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("DP_P"));
        }

        [Test]
        public void Dp_WinsOverQcf_WhenBothMatch()
        {
            // 6-2-3-6+P 同时满足 236（后三步）与 623（前三步）：
            // 设计规则"歧义时升龙优先"（Dp Priority 110 > Qcf 100）。
            // 此用例钉住 MotionDetector.Add 的降序排序——升序时波动会抢先消费按键。
            detector.Add(MotionLibrary.Qcf("QCF_P", ButtonMask.LP));
            detector.Add(MotionLibrary.Dp("DP_P", ButtonMask.LP));

            Push(5); Push(6); Push(2); Push(3); Push(6, ButtonMask.LP);

            Assert.That(Detect(), Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("DP_P"));
        }

        // ---- 双击冲刺（纯方向指令）----

        [Test]
        public void DashForward_DoubleTap_IsDetected()
        {
            detector.Add(MotionLibrary.DashForward());

            Push(6); Push(5); Push(6);

            Assert.That(Detect(), Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("DASH_F"));
        }

        [Test]
        public void DashForward_HoldingForward_DoesNotRetrigger()
        {
            detector.Add(MotionLibrary.DashForward());

            Push(6); Push(5); Push(6);
            Detect(); // 第一次识别
            Push(6);  // 继续按住前：最后一步不是"刚进入"，不得重复触发

            Assert.That(Detect(), Is.Empty);
        }

        [Test]
        public void DashForward_WithoutRelease_IsRejected()
        {
            detector.Add(MotionLibrary.DashForward());

            // 一直按前没有中间松开步（5/2/8），不构成双击
            Push(5); Push(6); Push(6); Push(6);

            Assert.That(Detect(), Is.Empty);
        }
    }
}
