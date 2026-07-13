using System;
using UnityEngine;

namespace Domain.Infrastructure.Battle
{
    public enum MovementState : byte
    {
        Idle,
        WalkForward,
        WalkBackward,
        DashStartup,   // 冲刺起步（不可取消，是冲刺的风险窗口）
        Dashing,       // 冲刺推进中
        Running,       // 跑步：冲刺后持续按住前 → 转入跑步循环
        DashRecovery,  // 冲刺/跑步收招
        BackDash,      // 后跃步（带无敌帧）
        JumpSquat,     // 起跳预备帧：不可取消，跳跃的风险来源
        Airborne,      // 空中（含空中冲刺）
        Landing,       // 落地硬直
    }

    /// <summary>
    /// 移动参数。全部数值集中在这里，调手感只改这一个类。
    /// 默认值按经典街霸的量级设定（角色约 2 单位高，站立间距约 1.5 单位）。
    /// </summary>
    [Serializable]
    public sealed class MovementConfig
    {
        // ---- 行走 ----
        [Tooltip("前进速度（单位/帧）")]
        public float WalkForwardSpeed = 0.035f;

        [Tooltip("后退速度。惯例上比前进慢——退比进难，保证进攻方有优势")]
        public float WalkBackwardSpeed = 0.028f;

        // ---- 冲刺 / 跑步 ----
        [Tooltip("冲刺起步帧：这几帧不可取消、无位移，是冲刺被确反的窗口")]
        public int DashStartupFrames = 3;

        [Tooltip("冲刺推进帧数")]
        public int DashFrames = 12;

        [Tooltip("冲刺速度（单位/帧）")]
        public float DashSpeed = 0.075f;

        [Tooltip("冲刺/跑步收招帧：这几帧不能出招不能防御")]
        public int DashRecoveryFrames = 4;

        [Tooltip("跑步速度。冲刺结束时若仍按住前 → 转入跑步循环（街霸三代式）")]
        public float RunSpeed = 0.06f;

        // ---- 后跃步 ----
        [Tooltip("后跃总帧数")]
        public int BackDashFrames = 22;

        [Tooltip("后跃无敌帧：第 1 帧到第 N 帧。这是后跃能的原因，也是它的价值所在")]
        public int BackDashInvulnFrames = 7;

        [Tooltip("后跃总位移（会按抛物线分配到各帧）")]
        public float BackDashDistance = 0.9f;

        // ---- 跳跃 ----
        [Tooltip("起跳预备帧：这几帧在地面且不可取消——跳跃的风险来源，被抓到就是确反")]
        public int JumpSquatFrames = 4;

        [Tooltip("起跳初速度（单位/帧）")]
        public float JumpVelocity = 0.16f;

        [Tooltip("重力（单位/帧²）")]
        public float Gravity = 0.008f;

        [Tooltip("前跳/后跳的水平速度")]
        public float JumpHorizontalSpeed = 0.045f;

        [Tooltip("落地硬直帧：这几帧不能行动。空中招落地的硬直另算")]
        public int LandingFrames = 3;

        // ---- 空中冲刺 ----
        [Tooltip("每次跳跃允许的空中冲刺次数")]
        public int AirDashCount = 1;

        [Tooltip("空中冲刺帧数")]
        public int AirDashFrames = 10;

        [Tooltip("空中冲刺速度")]
        public float AirDashSpeed = 0.08f;

        [Tooltip("空中冲刺期间是否免疫重力（罪恶装备式的滞空感）")]
        public bool AirDashIgnoresGravity = true;

        // ---- 场地 ----
        [Tooltip("地面高度")]
        public float GroundY = 0f;
    }
}