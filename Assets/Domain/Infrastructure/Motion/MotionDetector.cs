using System.Collections.Generic;
using Domain.Infrastructure.Input;

namespace Domain.Infrastructure.Motion
{
    public sealed class MotionDetector
    {
        private readonly List<MotionPattern> patterns = new List<MotionPattern>();

        public void Add(MotionPattern pattern)
        {
            patterns.Add(pattern);
            // 降序：高优先级先匹配。DetectAll 靠 consumedButtons 做歧义裁决——
            // 谁先匹配谁消费触发键，所以排序方向就是"升龙(110) > 波动(100)"规则本身。
            // （升序曾是 bug：6236+P 会被波动抢先消费，升龙永远出不来。）
            patterns.Sort((a,b) => b.Priority.CompareTo(a.Priority));
        }
        
        public void Clear() => patterns.Clear();


        public void DetectAll(InputBuffer buffer, bool facingRight, List<MotionPattern> results)
        {
            results.Clear();
            if (buffer.Count == 0) return;
            InputFrame latest = buffer.Latest;
            ButtonMask consumedButtons = ButtonMask.None;
            bool directionSlotUsed = false;
            for (int i = 0; i < patterns.Count; ++i)
            {
                MotionPattern p = patterns[i];
                if (p.TriggerButtons != ButtonMask.None)
                {
                    ButtonMask hit = latest.Pressed & p.TriggerButtons;
                    if (hit == ButtonMask.None) continue;
                    if ((hit & ~consumedButtons) == ButtonMask.None) continue;
                    if (!MatchSteps(buffer, p, facingRight)) continue;
 
                    consumedButtons |= hit;
                    results.Add(p);
                }
                else
                {
                    if (directionSlotUsed) continue;
                    if (!LastStepJustEntered(buffer, p, facingRight)) continue;
                    if (!MatchSteps(buffer, p, facingRight)) continue;
 
                    directionSlotUsed = true;
                    results.Add(p);
                }
                
                
            }
            
            
        }
        
        private static bool LastStepJustEntered(InputBuffer buffer, MotionPattern p, bool facingRight)
        {
            if (buffer.Count < 2) return false;
            buffer.TryGet(0, out InputFrame cur);
            buffer.TryGet(1, out InputFrame prev);
 
            ushort lastMask = p.Steps[p.Steps.Length - 1].DirMask;
            byte d0 = Normalize(cur.Direction, p, facingRight);
            byte d1 = Normalize(prev.Direction, p, facingRight);
            return (lastMask & Numpad.Bit(d0)) != 0 && (lastMask & Numpad.Bit(d1)) == 0;
        }
 
        /// <summary>
        /// 从最新帧向回回溯，按逆序逐步匹配方向序列。
        /// 每步在 [上一步匹配位置+1, +MaxGap] 的范围内搜索，整体不超过 TotalWindow。
        /// 蓄力步额外要求：从匹配帧继续向回，方向集合被连续保持 ChargeFrames 帧
        /// （蓄力保持可以超出 TotalWindow，窗口只约束"松开蓄力到出招"的距离）。
        /// </summary>
        private static bool MatchSteps(InputBuffer buffer, MotionPattern p, bool facingRight)
        {
            MotionStep[] steps = p.Steps;
            int age = 0; // 搜索游标：0 = 触发帧
 
            for (int i = steps.Length - 1; i >= 0; i--)
            {
                MotionStep step = steps[i];
                int searchLimit = age + step.MaxGap;
                if (searchLimit > p.TotalWindow) searchLimit = p.TotalWindow;
 
                bool found = false;
                for (int a = age; a <= searchLimit; a++)
                {
                    if (!buffer.TryGet(a, out InputFrame f)) break;
                    byte dir = Normalize(f.Direction, p, facingRight);
                    if ((step.DirMask & Numpad.Bit(dir)) == 0) continue;
 
                    if (step.ChargeFrames > 0 && !CheckCharge(buffer, step, p, facingRight, a))
                        continue;
 
                    found = true;
                    age = a + 1; // 前一步必须发生在更早的帧
                    break;
                }
                if (!found) return false;
            }
            return true;
        }
 
        private static bool CheckCharge(InputBuffer buffer, MotionStep step,
            MotionPattern p, bool facingRight, int startAge)
        {
            // 注意：蓄力期间若角色转向，这里用"当前朝向"统一镜像会有轻微误差。
            // 严谨做法是把"前/后"归一化后再写入 buffer，或蓄力量单独用计数器跟踪。
            // 起步阶段先接受这个简化。
            int held = 0;
            for (int a = startAge; buffer.TryGet(a, out InputFrame f); a++)
            {
                byte dir = Normalize(f.Direction, p, facingRight);
                if ((step.DirMask & Numpad.Bit(dir)) == 0) break;
                held++;
                if (held >= step.ChargeFrames) return true;
            }
            return false;
        }
 
        private static byte Normalize(byte worldDir, MotionPattern p, bool facingRight)
        {
            return (facingRight || !p.MirrorByFacing) ? worldDir : Numpad.Mirror(worldDir);
        }
    }
}