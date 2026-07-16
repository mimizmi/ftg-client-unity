using System;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum MovementState : byte
    {
        Idle,
        CrouchEnter,   // 一次性动画：下蹲过渡（站→蹲）。中途起身从对称位置接起身动画
        Crouch,        // 循环动画：蹲姿待机。无位移；价值 = 矮受击框躲上段 + 蹲姿技
        CrouchExit,    // 一次性动画：起身过渡（蹲→站）。中途再蹲从对称位置接下蹲动画
        WalkForward,   // 循环动画：帧号在 1..TotalFrames 之间循环
        WalkBackward,  // 同上
        Dash,          // 一次性动画：Startup=起步 / Active=推进 / Recovery=收招
        Run,           // 循环动画（需要跑步循环 clip）
        BackDash,      // 一次性动画，无敌帧写在 MoveData.InvulnFrom/To 里
        Jumping,       // 一次性动画：Startup=起跳预备 / Active=腾空 / Recovery=落地
    }

    /// <summary>
    /// 移动参数。全部数值集中在这里，调手感只改这一个类。
    /// 默认值按经典街霸的量级设定（角色约 2 单位高，站立间距约 1.5 单位）。
    /// </summary>
    [Serializable]
    public sealed class MovementConfig
    {
        public string IdleId = "Frank_FS4_Idle_Stand_Loop";

        /// <summary>蹲姿待机循环。</summary>
        public string CrouchId = "Frank_FS4_Idle_Crouch_Loop";

        /// <summary>
        /// 下蹲/起身过渡（一次性 clip）。三段式：CrouchEnter → Crouch(循环) → CrouchExit。
        /// 命名按语义：Crouching = 正在蹲下去（站→蹲），Standing = 正在站起来（蹲→站）。
        /// 若美术导出恰好相反，把这两个 Id 对调即可。留空 = 无过渡，直接进/出循环（可降级）。
        /// </summary>
        public string CrouchEnterId = "Frank_FS4_Crouching";
        public string CrouchExitId = "Frank_FS4_Standing";

        // ---- 地面移动（MoveId = Animator State = Clip 名）----
        public string WalkForwardId = "Frank_FS4_8Way_QuickWalk_F";
        public string WalkBackwardId = "Frank_FS4_8Way_QuickWalk_B";
        public string DashId = "Frank_FS4_Dash_Forward";
        public string BackDashId = "Frank_FS4_Dash_Backward";
 
        /// <summary>跑步循环。留空 = 无跑步（冲刺制，街霸四代/五代/六代与现代主流）。</summary>
        public string RunId = "";
 
        // ---- 跳跃 ----
        public string JumpNeutralId = "Frank_FS4_Jump_N_High_All";
        public string JumpForwardId = "Frank_FS4_Jump_F_High_All";
        public string JumpBackwardId = "Frank_FS4_Jump_B_High_All";
 
        // ---- 空中冲刺 ----
        /// <summary>空中冲刺。留空 = 无空中冲刺。</summary>
        public string AirDashId = "";
 
        /// <summary>每次跳跃允许的空中冲刺次数。</summary>
        public int AirDashCount = 1;
    }
}