using System;
using System.Collections.Generic;

namespace Domain.Infrastructure
{
    public sealed class DetectedCommand
    {
        public string Id;
        public int Priority;
        public int DetectedFrame;
        public int ExpireFrame;
    }
    public sealed class CommandQueue
    {
        public int BufferFrames = 8;
        private readonly List<DetectedCommand> pending = new List<DetectedCommand>();
        public int Count => pending.Count;

        public void Enqueue(string id, int priority, int currentFrame)
        {
            for (int i = 0; i < pending.Count; ++i)
            {
                if (pending[i].Id == id)
                {
                    pending[i].DetectedFrame = currentFrame;
                    pending[i].ExpireFrame = currentFrame + BufferFrames;
                    return;
                }
            }
            pending.Add(new DetectedCommand
            {
                Id = id,
                Priority = priority,
                DetectedFrame = currentFrame,
                ExpireFrame = currentFrame + BufferFrames,
            });
        }

        public void Tick(int currentFrame)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].ExpireFrame < currentFrame)
                    pending.RemoveAt(i);
            }
        }
        
        public bool TryConsume(out DetectedCommand command, Predicate<DetectedCommand> filter = null)
        {
            command = null;
            int bestIndex = -1;
 
            for (int i = 0; i < pending.Count; i++)
            {
                DetectedCommand c = pending[i];
                if (filter != null && !filter(c)) continue;
                if (command == null
                    || c.Priority > command.Priority
                    || (c.Priority == command.Priority && c.DetectedFrame > command.DetectedFrame))
                {
                    command = c;
                    bestIndex = i;
                }
            }
 
            if (bestIndex < 0) return false;
            pending.RemoveAt(bestIndex);
            return true;
        }
        
        public void Clear() => pending.Clear();
        
        public bool TryPeek(out DetectedCommand command, Predicate<DetectedCommand> filter = null)
        {
            command = null;
            for (int i = 0; i < pending.Count; i++)
            {
                DetectedCommand c = pending[i];
                if (filter != null && !filter(c)) continue;
                if (command == null
                    || c.Priority > command.Priority
                    || (c.Priority == command.Priority && c.DetectedFrame > command.DetectedFrame))
                {
                    command = c;
                }
            }
            return command != null;
        }
    }
}