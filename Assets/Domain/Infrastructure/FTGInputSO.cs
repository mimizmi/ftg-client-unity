using System;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Domain.Infrastructure
{
    [CreateAssetMenu(fileName = "InputAssets", menuName = "Input/InputAssets", order = 0)]
    public class FTGInputSO : ScriptableObject, FTGActions.IGameplayActions, FTGActions.IP1Actions, FTGActions.IP2Actions
    {
        [SerializeField, Range(0,1)] public int seat;
        public FTGActions FtgActions { get; private set; }
        public FTGActions.GameplayActions GameplayActions { get; private set; }
        public FTGActions.P1Actions P1Actions { get; private set; }
        public FTGActions.P2Actions P2Actions { get; private set; }
        private ButtonMask prevHeld;
        public int CurrentFrame { get; private set; }
        public InputBuffer Buffer { get; } = new InputBuffer(120);
        
        public event Action<InputFrame> FrameSampled = delegate { };
        public event Action<int, InputBuffer> LogicTick = delegate { }; 

        public void Initialize()
        {
            Shutdown();

            FtgActions = new FTGActions();
            GameplayActions = FtgActions.gameplay;
            GameplayActions.AddCallbacks(this);
            GameplayActions.Enable();
            
            P1Actions = FtgActions.p1;
            P2Actions = FtgActions.p2;
            if (seat == 0)
            {
                P1Actions.AddCallbacks(this);
                P1Actions.Enable();
            }
            else
            {
                P2Actions.AddCallbacks(this);
                P2Actions.Enable();
            }
            
            
            CurrentFrame = 0;
            prevHeld = ButtonMask.None;
        }

        public void Shutdown()
        {
            if (FtgActions != null)
            {
                GameplayActions.RemoveCallbacks(this);
                GameplayActions.Disable();
                if (seat == 0)
                {
                    P1Actions.RemoveCallbacks(this);
                    P1Actions.Disable();
                }
                else
                {
                    P2Actions.RemoveCallbacks(this);
                    P2Actions.Disable();
                }
                FtgActions.Dispose();
                FtgActions = null;
            }
        }

        public InputFrame GamePlaySample()
        {
            Vector2 mv = seat == 0 ? P1Actions.Move.ReadValue<Vector2>() : P2Actions.Move.ReadValue<Vector2>();
            int dx = Mathf.RoundToInt(mv.x);
            int dy = Mathf.RoundToInt(mv.y);
            byte dir = Numpad.FromAxes(dx, dy);
            ButtonMask held = ButtonMask.None;
            if (seat == 0 ? P1Actions.LP.IsPressed() : P2Actions.LP.IsPressed()) held |= ButtonMask.LP;
            if (seat == 0 ? P1Actions.HP.IsPressed() : P2Actions.HP.IsPressed()) held |= ButtonMask.HP;
            if (seat == 0 ? P1Actions.MP.IsPressed() : P2Actions.MP.IsPressed()) held |= ButtonMask.MP;
            if (seat == 0 ? P1Actions.LK.IsPressed() : P2Actions.LK.IsPressed()) held |= ButtonMask.LK;
            if (seat == 0 ? P1Actions.MK.IsPressed() : P2Actions.MK.IsPressed()) held |= ButtonMask.MK;
            if (seat == 0 ? P1Actions.HK.IsPressed() : P2Actions.HK.IsPressed()) held |= ButtonMask.HK;

            ButtonMask pressed = held & ~prevHeld;
            ButtonMask released = prevHeld & ~held;
            prevHeld = held;
            return new InputFrame
            {
                Frame = CurrentFrame,
                Direction = dir,
                Held = held,
                Pressed = pressed,
                Released = released
            };
            /*
            Vector2 mv = GameplayActions.Move.ReadValue<Vector2>();
            int dx = Mathf.RoundToInt(mv.x);
            int dy = Mathf.RoundToInt(mv.y);
            byte dir = Numpad.FromAxes(dx, dy);
            ButtonMask held = ButtonMask.None;
            if (GameplayActions.LP.IsPressed()) held |= ButtonMask.LP;
            if (GameplayActions.HP.IsPressed()) held |= ButtonMask.HP;
            if (GameplayActions.MP.IsPressed()) held |= ButtonMask.MP;
            if (GameplayActions.LK.IsPressed()) held |= ButtonMask.LK;
            if (GameplayActions.MK.IsPressed()) held |= ButtonMask.MK;
            if (GameplayActions.HK.IsPressed()) held |= ButtonMask.HK;

            ButtonMask pressed = held & ~prevHeld;
            ButtonMask released = prevHeld & ~held;
            prevHeld = held;
            return new InputFrame
            {
                Frame = CurrentFrame,
                Direction = dir,
                Held = held,
                Pressed = pressed,
                Released = released
            };*/
        }

        public void GamePlayInputTick()
        {
            CurrentFrame++;
            InputFrame inputFrame = GamePlaySample();
            Buffer.Push(inputFrame);
            FrameSampled.Invoke(inputFrame);
            LogicTick.Invoke(CurrentFrame, Buffer);
        }

        public void OnLK(InputAction.CallbackContext context)
        {
            
        }

        public void OnHK(InputAction.CallbackContext context)
        {
            
        }

        public void OnMK(InputAction.CallbackContext context)
        {
            
        }

        public void OnLP(InputAction.CallbackContext context)
        {
            
        }

        public void OnHP(InputAction.CallbackContext context)
        {
            
        }

        public void OnMP(InputAction.CallbackContext context)
        {
            
        }

        public void OnMove(InputAction.CallbackContext context)
        {
            
        }
    }
}