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

        // ---- 事件级按键锁存：修"快速轻点被 60Hz 轮询漏掉" ----
        // IsPressed() 只回答"这一瞬按着吗"：两次逻辑采样(16.7ms)之间完成的按下→抬起
        // 会被彻底丢掉，体感就是"必须按重(=按久)才有反应"。
        // InputSystem 的 performed 回调在按下瞬间触发（与帧率无关），这里锁存
        // "自上个逻辑帧以来按下过的键"，采样时并入 Pressed 并清空——短按永不丢失。
        // 方向仍按 60Hz 采样（方向是持续状态，丢失窗口极小；且事件重放会破坏确定性简单性）。
        private ButtonMask latchedPresses;
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
            latchedPresses = ButtonMask.None;
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

            // 轮询边沿 ∪ 事件锁存：正常按住的键两边都会命中（同帧合并，不重复）；
            // 采样间隙内"按下即松开"的短按只有锁存能抓到（此时 Held 为 false 是正确语义）。
            ButtonMask pressed = (held & ~prevHeld) | latchedPresses;
            latchedPresses = ButtonMask.None;
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

        /// <summary>
        /// 按下瞬间锁存。回调同时注册在 gameplay 图和本座位图上（Initialize），
        /// 必须按座位过滤——否则 gameplay 图或另一座位的同名动作会串进来。
        /// </summary>
        private void LatchPress(InputAction.CallbackContext context, ButtonMask button)
        {
            if (!context.performed) return;
            if (context.action.actionMap != (seat == 0 ? P1Actions.Get() : P2Actions.Get())) return;
            latchedPresses |= button;
        }

        public void OnLK(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.LK);

        public void OnHK(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.HK);

        public void OnMK(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.MK);

        public void OnLP(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.LP);

        public void OnHP(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.HP);

        public void OnMP(InputAction.CallbackContext context) => LatchPress(context, ButtonMask.MP);

        public void OnMove(InputAction.CallbackContext context)
        {
            // 方向不锁存：持续状态按 60Hz 采样已足够（见 latchedPresses 注释）
        }
    }
}