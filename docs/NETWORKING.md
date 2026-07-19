# 网络阶段：确定性 → protobuf 协议 → Go 权威服务器 → 回滚

本作的玩法纵轴之上，是一条**为回滚网络设计的确定性架构**横轴。目标终局：
帧同步（lockstep）打通后升级为预测/回滚（rollback），权威模拟跑在 Go 服务器上。

## 路线（N1–N5）

| 阶段 | 内容 | 状态 |
|------|------|------|
| **N1** | 定点数地基：Q16.16 `Fix` / `FixVec2` + 语义契约测试（=Go 对拍基准） | ✅ 已验证 |
| **N2** | 模拟核心整体换型：`Position`/RootMotion/碰撞盒全定点，float 只留 JSON 装载与表现渲染两个边界 | ✅ 已验证 |
| **N3** | **protobuf 协议**：跨语言唯一契约源（本文档） | 🚧 进行中 |
| **N4** | Go 无头模拟移植 + 跨语言逐帧哈希**对拍** | ⬜ |
| **N5** | 先帧同步（lockstep），再预测/回滚（rollback） | ⬜ |

为什么这个顺序：**确定性是回滚的地基**。同样的输入必须逐比特复现同样的状态——
否则回滚重放会 desync。N1/N2 把浮点这个最大的非确定性源铲除；N3 把"输入流"和
"状态快照"变成跨语言（C# ↔ Go）逐位一致的线格式；N4 用两套语言的模拟跑同一份
输入夹具、逐帧比对哈希，证明移植没引入分歧；N5 才在这块可信地基上做网络同步。

---

## N3 protobuf 协议

### 契约文件（唯一真源）

```
proto/
  ftg/v1/combat.proto   # 输入 / 配置 / 回放 / 定点向量 / 状态快照
  ftg/v1/sync.proto     # 逐帧哈希日志（对拍与回滚校验）
  generate.ps1          # Windows codegen（protoc → C#，server/ 存在时并生成 Go）
  generate.sh           # Linux/macOS/CI codegen
```

C# 与 Go 两侧都从**同一份 `.proto`** 用官方 `protoc` 生成，谁都不手改生成码。

### 三条设计红线

1. **契约唯一**：`.proto` 是 C#（客户端）与 Go（服务器/对拍）的唯一真源。
   任一侧手改生成代码 = desync 的种子。
2. **定点上线，绝不传 float**：`Fix` 是 Q16.16 的 `int raw`；线格式一律 `sfixed32`
   存 raw（固定 4 字节、无 zigzag 歧义，C# `int` ↔ Go `int32` 逐位一致）。
   float 只活在客户端两个边界（JSON 装载、表现层渲染），永不进协议。
   例：位置 `-1.0` → `x_raw = -65536`；`0.5` → `raw = 32768`。
3. **枚举序数对齐**：proto 枚举序数与 C# `byte` 枚举**逐一对齐**
   （`FighterStatus` 0-4、`MovementState` 0-9）。适配层因此只是一次整型 `cast`，
   不查名字。**改 C# 枚举顺序 = 改协议**，必须同步改 `.proto`。

### 消息一览

- `Input{direction, held, pressed}` — 单人一 tick 被消费的输入（对齐 `ReplayInputFrame`）。
- `FrameInputs{frame, p1, p2}` — 一帧双方输入 = 帧同步/回滚最小传输单元。
- `BattleConfig` / `MatchSetup` — 回合规则 + 对阵开局头（含协议版本）。
- `Replay{setup, repeated FrameInputs}` — 整场序列化 = **N4 对拍的输入夹具**。
- `FixVec2{x_raw, y_raw}` / `FighterSnapshot` / `BattleSnapshot` — 定点向量与确定性状态快照
  （`FighterSnapshot` 字段集 = 测试 `HashFighter` 的确定性字段全集，也是 N5 的回滚存档单元）。
- `FrameHash` / `HashLog` — 逐帧哈希日志，C# 与 Go 各吐一份、diff 即对拍结论。

---

## 一次性设置（客户端 C# 侧）

> 你已选定**官方 Google.Protobuf 运行时**路线。下面三步做完，工程即可编译生成代码。

### 1. 安装 protoc

从 <https://github.com/protocolbuffers/protobuf/releases> 下 `protoc-*-win64.zip`，
解压后把 `bin\protoc.exe` 加入 PATH。验证：`protoc --version`。

### 2. 把 Google.Protobuf 运行时导入 Unity

**推荐 · NuGetForUnity**（自动处理 `System.Memory` 等依赖）：
装 NuGetForUnity（UPM 加 `https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity`），
菜单 `NuGet → Manage NuGet Packages`，搜 `Google.Protobuf` 安装。

**备选 · 手动放 DLL**：从 nuget.org 取 `Google.Protobuf`（netstandard2.0/2.1 lib），
`Google.Protobuf.dll` 丢进 `Assets/Plugins/Google.Protobuf/`。
Player Settings 的 Api Compatibility Level 需 **.NET Standard 2.1**
（Unity 内置 `Span`/`Memory`，protobuf 依赖即在框架内解析）。

### 3. 生成 C# 代码

```powershell
pwsh proto/generate.ps1
```

生成物落到 `Assets/Domain/Net/Generated/*.g.cs`。回到 Unity 等编译。

### IL2CPP 防裁剪（出包前才需要，编辑器 Play 不需要）

protobuf 反射会被 IL2CPP 的托管代码裁剪误删。届时加 `Assets/Domain/Net/link.xml`：

```xml
<linker>
  <assembly fullname="Google.Protobuf" preserve="all" />
  <assembly fullname="FTG.Net" preserve="all" />
</linker>
```

---

## Batch B（已交付）

- **`Assets/Domain/Net/FTG.Net.asmdef`** — 独立程序集，引用 `FTG.Core` +
  自动引用的 Google.Protobuf 插件，收纳生成码与适配层（**不污染纯模拟核心 FTG.Core**）。
- **适配层** `Domain.Net`：
  - `ReplayProtoCodec` — `ReplayData ↔ ftg.v1.Replay`（`ToBytes`/`FromBytes` 收口，
    protobuf 成为回放的第二序列化 + 对拍输入夹具导出/导入）。
  - `SnapshotProtoCodec` — `FixVec2`（sfixed32 存 Raw）、`FighterState → FighterSnapshot`、
    `BattleSimulation → BattleSnapshot`（含规范哈希）。
  - `StateHasher` — 逐帧状态哈希的**规范实现**（= 对拍契约，Go 侧逐字节镜像它）。
- **`ProtoCodecTests`**（EditMode，引用 `FTG.Net` + `Google.Protobuf.dll`）5 条：
  1. `Replay` protobuf 往返 → 喂回确定性模拟，逐帧哈希与原局**完全一致**（600 帧）。
  2. `StateHasher` ≡ 确定性测试内联哈希（改一处即红）。
  3. 枚举序数守卫：proto 枚举 ≡ C# `byte` 枚举逐值断言（改序不同步改 `.proto` 即红）。
  4. `FixVec2` 往返：`x_raw`/`y_raw` 逐位不变（含过线字节）。
  5. `BattleSnapshot` 捕获确定性状态 + 哈希，过 protobuf 线不变。

这些测试是 N3 协议的安全网；**N4** 再引入 Go 侧做真正的跨语言对拍
（同一 `Replay` 夹具喂两套模拟，逐帧 `HashLog` 比对）。

---

## N4 — 跨语言帧哈希对拍（已交付）

`server/`（Go module `ftgserver`）把整条确定性模拟核心从 C# 移植到 Go：定点数
（`sim/fixed` Q16.16，与 `Fix.cs` 逐位一致）→ 输入/搓招（`sim/input`、`sim/motion`）→
招式机/碰撞/推挡/回合（`sim/combat`）→ 状态哈希（`sim/statehash`，逐字节镜像 C#
`Domain.Net.StateHasher`）。`sim/duel.RunReplay` 用一份 `Replay` 夹具驱动 Go 模拟，
逐帧产出 `FrameHash`。

**对拍闭环**：Unity 编辑器菜单 `FG/导出对拍夹具` 用 `TestBattleFactory` 跑 180 帧，
写 `duel_replay.pb`（输入）+ `duel_hashlog.pb`（C# 规范哈希）到 `server/testdata`；
Go 侧 `go test ./sim/duel/` 用**同一份** `Replay` 跑自己的模拟，逐帧比对 C# 的 `HashLog`。
**结果：180 帧逐帧逐位一致，零分歧帧。** 两套独立实现的确定性由此被钉死——任一侧漂移
一个 bit，测试立刻指出具体是哪一帧、哪个字段。

## N5 — 帧同步与预测回滚（已交付，`server/sim/lockstep`）

模拟核心只透过「座位」读输入（`sim/seat`：脚本/回放/网络三种实现，喂给核心的东西
无法区分）。`lockstep.Transport` 把「输入信道」抽象成 `Send`/`Drain`，peer 逻辑与传输解耦。

- **① 帧同步（lockstep）**：`Peer` 各持一份完整模拟，本端输入延迟 `D` 帧生效（藏住网络
  往返），某帧只有双方输入都到齐才 `Tick`。测试：两端逐帧 `StateHash` 逐位一致，且等于
  单机 `duel.RunReplay` 参照；扫 `D/L` 组合（含 `D<L`）验证延迟只影响节奏不影响正确性。
- **② 预测回滚（rollback）**：`combat.BattleSimulation.Clone`（深快照，只拷每帧可变态、
  共享不可变配置）是存档/还原引擎。`RollbackPeer` 持两份模拟——`confirmed`（仅真输入推进、
  永不回退）+ `predicted`（每帧从 `confirmed` 克隆、用预测输入跑到 head）；本地输入立即
  生效（`D=0`），远端未到即预测（重复上一帧），真输入到达经 `advanceConfirmed` 定稿。
  测试：无论预测错多少、回滚多深，`confirmed` 轨迹恒等于单机参照；回滚窗口随延迟单调加深
  （L=0/2/5 → 回滚 1/2/5 帧），`Corrections` 证明确有误预测被修正。

**核心主张**：回滚只改变「何时看到正确结果」，绝不改变「最终的正确结果」。`StateHasher`
从 N4 的对拍探针一路复用为 N5 的 desync 守卫。

## N6 — 真网络（已交付，`server/netcode` + `server/cmd`）

拓扑 = **中继权威**：Go 服务器撮合、分配座位（P1/P2）、下发权威 `MatchSetup`、转发输入；
回滚在客户端本地跑。这样「Go 权威服务器」与「回滚」两不冲突。

- **协议** `proto/ftg/v1/net.proto`（跨语言唯一契约源）：`Packet` = `oneof{JoinRequest,
  JoinResponse, InputDatagram}`。抗丢包两条纪律：① 每个 `InputDatagram` 冗余携带最近 `W`
  帧（单丢由下一报文补齐）；② 每帧显式带绝对 `frame`，收端去重 + 按帧归位。
- **`netcode.RelayServer`**：单 goroutine UDP 读循环，握手分座 + 转发。
- **`netcode.ClientTransport`**：客户端侧的 `lockstep.Transport` 实现（UDP + 冗余窗口 +
  去重）。**关键：N5 的回滚 peer 一行未改**——只是把进程内 `Pipe` 换成了它。
- **`cmd/server` + `cmd/client`**：可执行入口，开一个服务器 + 两个客户端进程即成一局。

**验证**：`go test ./netcode/`（`-race` 干净）起真 UDP loopback，两端 `confirmed` 轨迹
120 帧逐位一致且等于单机参照。三进程实跑（server + 2 client）180 帧后**两端末帧哈希相同**
——真网络栈（序列化 / socket / 异步收发）下，确定性回滚同样成立。

### 网络健壮性（丢包 / 抖动 / 乱序）

「冗余窗口 + 去重」的簿记抽成了 `lockstep.Windower`（单一真源）：线上 `ClientTransport` 与
进程内 `RedundantChannel` 共用它。真 UDP 无法可控地注入丢包，故另建 `lockstep` 内一条
**确定性、可复现**的 stepped-clock 恶劣链路（`NetConditions{Latency, Jitter, LossRate, Seed}`，
种子固定），把「回滚 + 冗余」按住反复拷打：

- **抗丢包/抖动/乱序**：10%–30% 丢包 + 抖动（后发先到=乱序）下，`confirmed` 轨迹仍逐位等于
  单机参照——丢包只推迟「何时确认」，不改「确认成什么」；回滚窗口随丢包加深（30% 丢包时达 9 帧）。
- **冗余窗口的价值（对照）**：同样 30% 丢包，**无冗余（窗口=1）在第 3~8 帧即永久卡死**
  （被丢的帧无人重发），**冗余（窗口=32）照常收敛且逐位正确**——一测见分晓。

> 至此 N1→N6 全线打通：定点数 → 模拟核心 → protobuf 协议 → Go 无头移植 →
> 跨语言帧哈希对拍 → 帧同步 → 预测回滚 → 真 UDP。「为回滚网络设计的确定性架构」
> 从架构声明变成可运行、可测试、可联机的铁证。
