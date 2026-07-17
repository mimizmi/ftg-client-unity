using System;
using System.Text;
using XLua;

namespace Domain.Service.Lua
{
    /// <summary>
    /// Lua 虚拟机服务：全局唯一 LuaEnv 的生命周期管理 + 脚本加载 + GC 驿站。
    ///
    /// 【边界纪律——为什么战斗核心永不 Lua 化】
    /// 确定性模拟要求逐位一致与回滚性能：Lua 的浮点行为、GC 停顿、跨语言边界开销
    /// 全都踩在红线上。Lua 只写【运营外围】——UI 流程、公告、活动这类改动最频繁、
    /// 又不参与确定性的逻辑。"哪些代码该热更"的边界划分本身就是架构决策。
    ///
    /// 【加载链路】require "notice" → Addressables address "Lua/notice"（资产 notice.lua.txt）。
    /// 与资源/数据共用同一条 remote catalog 热更管线：改 Lua → Update a Previous Build →
    /// 重启即生效，逻辑热更不发版。
    ///
    /// 【运行模式】反射模式（编辑器/Mono 出包够用）；IL2CPP 出包前需 XLua/Generate Code，
    /// 生成的 wrap 目录要移出 XLua.asmdef（它引用游戏类型，会造成环）——见 ENGINEERING.md。
    /// </summary>
    public sealed class LuaService : IDisposable
    {
        private const float GcIntervalSeconds = 5f; // xLua 建议对 LuaEnv.Tick 节流

        private readonly LuaEnv env;
        private readonly Func<string, string> readText;
        private float lastGcTime;

        /// <summary>裸环境句柄——注册 C# 回调/取全局表用。业务模块优先走 Require。</summary>
        public LuaEnv Env => env;

        /// <summary>readText：key（如 "Lua/boot"）→ 脚本源码，取不到返回 null。运行时传 Addressables 读取器。</summary>
        public LuaService(Func<string, string> readText)
        {
            this.readText = readText;
            env = new LuaEnv();
            env.AddLoader(LoadScript);
        }

        private byte[] LoadScript(ref string filepath)
        {
            string source = readText($"Lua/{filepath}");
            return source != null ? Encoding.UTF8.GetBytes(source) : null; // null = 交给下一个 loader/报 not found
        }

        /// <summary>require 语义执行模块（自带缓存，重复调用不重跑）。返回模块返回值。</summary>
        public object[] Require(string module)
            => env.DoString($"return require '{module}'", module);

        /// <summary>由宿主每帧驱动：节流执行 Lua 侧全量 GC（清理 C# ↔ Lua 引用桥）。</summary>
        public void Tick(float unscaledTime)
        {
            if (unscaledTime - lastGcTime < GcIntervalSeconds) return;
            lastGcTime = unscaledTime;
            env.Tick();
        }

        public void Dispose() => env.Dispose();
    }
}
