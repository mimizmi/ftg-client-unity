using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Domain.Infrastructure;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;

namespace Domain.Service
{
    public class FightingInputController : MonoBehaviour
    {
        [SerializeField] private FTGInputSO inputAssets;
        [Header("Settings")] 
        public const int TickRate = 60;
        private const float TickDelta = 1f / TickRate;
        private const float MaxAccumulated = 0.25f;
        private float accumulator;
        
        [Header("Character")]
        public bool FacingRight = true;
        public MotionDetector Detector { get; } = new MotionDetector();
        public CommandQueue Commands { get; } = new CommandQueue();
        private readonly List<MotionPattern> detectResults = new List<MotionPattern>(4);
        public event Action<DetectedCommand> CommandDetected = delegate { };
        public event Action<int> Ticked = delegate { };
        public InputBuffer Buffer => inputAssets.Buffer;
        public int CurrentFrame => inputAssets.CurrentFrame;
        
        [SerializeField] private bool selfDriven = true;

        public bool SelfDriven
        {
            get => selfDriven;
            set => selfDriven = value;
        }

        private void Awake()
        {
            inputAssets.Initialize();
        }

        private void OnDestroy()
        {
            inputAssets.Shutdown();
        }

        private void OnEnable()
        {
            inputAssets.FrameSampled += Log;
            inputAssets.LogicTick += GamePlayLogicTick;
        }

        private void OnDisable()
        {
            inputAssets.FrameSampled -= Log;
            inputAssets.LogicTick -= GamePlayLogicTick;
        }

        public void ManualTick() => inputAssets.GamePlayInputTick();

        private void Update()
        {
            if (!selfDriven) return;
            accumulator += Time.deltaTime;
            if (accumulator > MaxAccumulated) accumulator = MaxAccumulated;

            while (accumulator >= TickDelta)
            {
                accumulator -= TickDelta;
                inputAssets.GamePlayInputTick();
            }
        }

        private void Log(InputFrame inputFrame)
        {
            if (inputFrame is { Held: ButtonMask.None, Pressed: ButtonMask.None, Released: ButtonMask.None })
                return;
            Debug.Log(inputFrame.ToString());
        }

        private void GamePlayLogicTick(int currentFrame, InputBuffer buffer)
        {
            Commands.Tick(currentFrame);
            Detector.DetectAll(buffer, FacingRight, detectResults);
            for (int i = 0; i < detectResults.Count; i++)
            {
                MotionPattern p = detectResults[i];
                Commands.Enqueue(p.Id, p.Priority, currentFrame);
                CommandDetected.Invoke(new DetectedCommand
                {
                    Id = p.Id,
                    Priority = p.Priority,
                    DetectedFrame = currentFrame,
                    ExpireFrame = currentFrame + Commands.BufferFrames,
                });
            }
            Ticked.Invoke(currentFrame);
        }
    }
}