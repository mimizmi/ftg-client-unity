using System;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum MovementState : byte
    {
        Idle,
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
        // ---- 地面移动（MoveId = Animator State = Clip 名）----
        public string WalkForwardId = "Frank_FS4_8Way_QuickWalk_F";
        public string WalkBackwardId = "Frank_FS4_8Way_QuickWalk_B";
        public string DashId = "Dash";
        public string BackDashId = "BackDash";
 
        /// <summary>跑步循环。留空 = 无跑步（冲刺制，街霸四代/五代/六代与现代主流）。</summary>
        public string RunId = "";
 
        // ---- 跳跃 ----
        public string JumpNeutralId = "Frank_FS4_Jump_N_High_All";
        public string JumpForwardId = "JumpForward";
        public string JumpBackwardId = "JumpBackward";
 
        // ---- 空中冲刺 ----
        /// <summary>空中冲刺。留空 = 无空中冲刺。</summary>
        public string AirDashId = "";
 
        /// <summary>每次跳跃允许的空中冲刺次数。</summary>
        public int AirDashCount = 1;
    }
}