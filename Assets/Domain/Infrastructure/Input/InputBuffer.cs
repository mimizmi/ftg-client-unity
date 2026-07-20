namespace Domain.Infrastructure.Input
{
    public class InputBuffer
    {
        private readonly InputFrame[] frames;
        private int head = -1; // 最新元素下标
        private int count;

        public InputBuffer(int capacity = 120)
        {
            frames = new InputFrame[capacity];
        }
        
        public int Capacity => frames.Length;
        public int Count => count;
        
        // 空缓冲返回 default（防御：正常时序下每次读取前必有当帧 Push；
        // 清空只发生在开战装配期，此后第一个逻辑帧就会推入新帧）
        public InputFrame Latest => head < 0 ? default : frames[head];

        public void Push(in InputFrame frame)
        {
            head = (head + 1) % frames.Length;
            frames[head] = frame;
            if (count < frames.Length) count++;
        }
        
        public bool TryGet(int framesAgo, out InputFrame frame){
            if (framesAgo < 0 || framesAgo >= count)
            {
                frame = default;
                return false;
            }

            int idx = head - framesAgo;
            if (idx < 0) idx += frames.Length;
            frame = frames[idx];
            return true;
        }

        public void Clear()
        {
            head = -1;
            count = 0;
        }

        /// <summary>深拷贝缓冲（回滚存档）：帧数组与读写游标整体复制，与原缓冲独立演进。</summary>
        public InputBuffer Clone()
        {
            var copy = new InputBuffer(frames.Length);
            System.Array.Copy(frames, copy.frames, frames.Length);
            copy.head = head;
            copy.count = count;
            return copy;
        }
    }
}