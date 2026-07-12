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
        
        public InputFrame Latest => frames[head];

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
    }
}