# 工程内幕（贡献者文档）

Unity 2022.3.61f3 · Loxodon Framework (MVVM) · Unity Input System · Addressables 1.22

> 面向读代码的人。项目介绍与技术亮点见根目录 [README](../README.md)，性能数据见 [PERFORMANCE](PERFORMANCE.md)。

---

## 一、分层架构（asmdef 编译期强制）

```
┌─ FTG.Core（Assets/Domain/Infrastructure，纯模拟，零外部依赖） ─┐
│  InputTypes    按键掩码、单帧输入快照、数字键盘记法             │
│  InputBuffer   环形缓冲（120 帧历史，零 GC）                   │
│  MotionPattern 搓招模式定义 + 常用招式库                       │
│  MotionDetector 回溯匹配、蓄力、镜像、优先级吞并（升龙>波动）    │
│  CommandQueue  指令队列（预输入缓冲，手感来源）                 │
│  IInputSeat    座位抽象（真人/脚本/回放/未来网络帧的注入点）     │
│  BattleSimulation 纯 C# 战斗模拟（回合/计时/连击/事件）         │
│  FighterState  角色状态机（招式 + 移动 + 受击统一调度）          │
│  MoveData / MoveTable / BoxData / CollisionResolver           │
│  MovementController / PushboxResolver                         │
│  BoxDataLoader JSON 解析注入（文本源【注入式】：运行时喂        │
│                Addressables，测试喂 File，Go 服务器喂文件）     │
│  FighterDefinition  角色数据仓库（数据唯一出处）                │
│  Replay/       录制·序列化·回放（输入流即存档，FTGR 二进制）     │
└───────────────────────────────────────────────────────────────┘
┌─ FTG.Runtime（Assets/Domain 其余：服务与表现） ────────────────┐
│  GameLauncher   应用组合根（全局服务注册）                      │
│  HotUpdater     启动热更（catalog 检查→增量下载→进度上报）       │
│  AddressablesTextReader  帧数据 JSON 的 Addressables 文本源     │
│  BattleBootstrap 战斗组合根（角色包按地址异步装配，可重入）       │
│  BattleLoop     60Hz 固定步长驱动器（纯驱动，模拟在 Core）       │
│  FightingInputController  座位设备（场景常驻）                  │
│  FighterView    表现层傀儡（动画只是显示器）                    │
│  FighterAnimationPlayer  Playables 直驱图（Clip 名 = MoveId）   │
│  FX/            RendererFlasher(手写HLSL) · HitSparkPool ·     │
│                 CameraShaker(trauma) · BattleFxController      │
│  ReplayFileStore 回放存档（persistentDataPath）                │
└───────────────────────────────────────────────────────────────┘
┌─ FTG.UI（Assets/UI，Loxodon MVVM） ───────────────────────────┐
│  UIManager      5 层 Canvas + 层级栈 + 生命周期                │
│  IUIAssetLoader Resources/Addressables 可换后端（引用计数）      │
│  GameFlowController  菜单→选人→战斗→结算→回放 状态机            │
│  BattleHud / MainMenu / CharacterSelect / Result / Loading    │
└───────────────────────────────────────────────────────────────┘
┌─ FTG.Editor（工具链） ────────────────────────────────────────┐
│  HitboxEditor           判定框可视化编辑（核心工具）            │
│  BatchRootMotionBaker / RootMotionBaker  位移烘焙              │
│  AnimatorContractValidator  动画契约校验                       │
│  FrameDataDiagnostics   帧数据 vs Clip 时长诊断                │
│  AddressablesBuildStep  出包前自动构建内容（演示包模式）         │
└───────────────────────────────────────────────────────────────┘
┌─ FTG.Tests.EditMode（30+ 用例） ──────────────────────────────┐
│  确定性双跑帧哈希 · 回放逐帧一致 · 搓招表 · 回合流程 · 连击      │
└───────────────────────────────────────────────────────────────┘
```

依赖方向：`UI → Runtime → Core`，反向引用编译不过——模拟纯净是编译器保证的，不靠自觉。

---

## 二、每帧执行顺序（BattleSimulation.Tick）

```
① 朝向同步       用位置关系决定朝向（搓招镜像依赖它）
② 输入采样       双方同帧采样 → 搓招检测 → 指令入队（冻结阶段也采样：回放流连续性）
③ 状态推进       招式状态机 → 移动状态机（顺序不能反）
④ 推挡解算       防重叠 + 版边约束（位置先解算干净）
⑤ 攻防裁决       无敌 → 当身 → 投/拆投 → 拒止 → 防御 → 命中/CH（接触点也在这层算）
⑥ 帧末广播       HitEvent / TickFinished（表现层订阅；回放录制也挂这里）
```

---

## 三、核心设计原则

### 1. 位置权威归逻辑，动画只是显示器
`Animator` 的 **Apply Root Motion 必须取消勾选**（`FighterView.Bind` 会强制关闭）。
位移是 `MoveData.RootMotion` 里的帧数据，由 `FighterState` 结算。
让 Animator 驱动位置会直接毁掉帧确定性，回滚网络无法实现。

### 2. 两个 ID 不是一回事
- `MotionPattern.Id` = **指令名**（玩家搓了 236+拳）
- `MoveData.MoveId` = **招式名** = Animation Clip 名（= JSON 键）

中间由 `MoveTable` 解析：同一指令按键强度/姿态/取消来源 → 不同招式。

### 3. 反制系统的三层信息源
| 层 | 用途 | 代码位置 |
|---|---|---|
| 状态层（帧数据） | Counter Hit、确反、克制 | `FighterState.Phase` / `MoveFrame` |
| 碰撞层（裁决管线） | 所有判定的锚点 | `CollisionResolver.Judge()` |
| 输入层（回看缓冲） | 拒止、拆投、AI 假人 | `InputQuery` |

**90% 的反制不需要读按键**——读对方"正在出什么招、第几帧"就够了。

### 4. Messenger 单向红线
Messenger 只做「核心 → 表现层」的广播。任何**改动战斗状态**的逻辑（AI、假人）
必须挂 `TickFinished` C# 事件，保证帧内顺序确定——这是回滚网络的前提。

### 5. 组合根原则
只有 `GameLauncher`、`BattleBootstrap`、View 脚本能接触服务容器。
其余业务类一律构造注入，绝不服务定位。

### 6. 数据格式：JSON 而非 ScriptableObject
Go 服务器要跑权威模拟，必须读同一份帧数据。判定框/位移直接落地 JSON，
Unity 与 Go 共用一份真相。文本来源注入式（Addressables/File），Core 不知道资源系统存在。

### 7. 座位与角色分离
**输入属于"座位"，不属于"角色"**：`FightingInputController` 是场景常驻对象
（P1Seat/P2Seat），绝不挂在角色 Prefab 上。角色 Prefab 只带表现（模型 + Animator +
FighterView），零场景引用。`BattleBootstrap` 是唯一的装配点：按地址异步加载角色包 →
构造 `FighterState` → `FighterView.Bind` 接线。`IInputSeat` 让真人/脚本/回放/网络帧
从同一个口注入——回放系统就是白捡的（换个座位实现）。

### 8. 表现层是只读观察者
HUD（MVVM 绑定）、FX（火花/震屏/溶解）、回放录制，全部只订阅模拟事件，
绝不反向写模拟。战斗随流程反复开/停，观察者自动重绑。

---

## 四、场景接线（GameFlow 完整流程模式）

| 对象 | 组件 | 说明 |
|---|---|---|
| GameLauncher | GameLauncher (+HotUpdater) | 全局服务 + 启动热更 |
| BattleLoop | BattleLoop | 中央时钟，无需拖引用 |
| P1Seat / P2Seat | FightingInputController | 各绑不同 Action Map |
| BattleBootstrap | BattleBootstrap | 拖 loop + 两个 Seat；角色按地址约定 `Characters/{Id}` 加载，**autoStart 取消勾选** |
| UIManager | UIManager | 勾选 Use Addressables |
| GameFlow | GameFlowController | 拖 ui/bootstrap/loop/hotUpdater |
| Main Camera | CameraShaker | 震屏（可选） |
| FX | BattleFxController + HitSparkPool | 打击感总控（可选） |

角色 Prefab 检查清单：Animator + FighterView（Use Playables 默认开）；
**没有** FightingInputController；没有任何场景引用；已标记 Addressable（`Characters/{Id}`）。

---

## 五、动画契约（M4 起：Playables 直驱）

`FighterView.usePlayables`（默认开）：`FighterAnimationPlayer` 用 PlayableGraph
（Manual 更新模式）直驱 clip，播放头完全由逻辑帧经 `SetTime + Evaluate(0)` 钉住。

**契约只剩一条：Clip 名 = MoveId**（AnimatorController 退化成 clip 清单，仅供采集，
不连 Transition、不用 Parameter；运行时控制器被卸下）。占位借用（如前跳暂借垂直跳
clip）在 FighterView 的 **Clip Aliases** 表里配映射。

**Clip 要求**：60fps 采样；帧数 = 帧数据 TotalFrames（动画第 N 帧 == 逻辑第 N 帧）；
招式 Clip 不勾 Loop Time；待机类勾 Loop。

旧路径（Animator 状态机，State 名 = MoveId，speed=0 傀儡 + CrossFade）保留在开关后面，
`FG/Animator Contract Validator` 仍可校验它。

---

## 六、工作流

### 加一个新招
1. `FighterDefinition` 里加 `MoveEntry`（指令/按键/姿态/连招规则）
2. 同处加 `MoveData`（发生/持续/恢复/伤害/硬直）
3. Clip 改名为 MoveId，放进角色的 AnimatorController（clip 清单）
4. `FG/Hitbox Editor` 画判定框 → 保存 JSON 到 `Assets/BoxData/`
5. 帧数据 JSON 已是 Addressables 资产——改完 **Update a Previous Build 即可热更**，不用发版

### 位移数据（单一数据源：JSON）
位移烘进 `Assets/BoxData/{角色}_rootmotion.json`，运行时由 `BoxDataLoader` 注入。
- **批量**：`FG/Batch Root Motion Baker`；**单个**：`FG/Hitbox Editor` 内「烘焙位移」
- 前进轴选 **Z**；两个工具合并式写入，重烘位移不毁判定框

⚠ **顺序：先烘位移，再画判定框**（判定框坐标相对逻辑原点 = 位移累加；
先画框会把动画漂移吃进坐标，产生"幽灵框"）。

### 资源与热更
- 地址约定：角色 `Characters/{Id}`、帧数据 `BoxData/{Id}_boxes|_rootmotion`、UI `UI/{名}`
- 标签 `preload`：启动预下载范围
- 本地热更演示：Hosting 服务（127.0.0.1 + 固定端口）+ Remote 组 + Build Remote Catalog；
  改 JSON → Build/**Update a Previous Build** → 重启进游戏即生效
- 出 Windows 演示包：勾上菜单 `FG/Build/演示包模式`（全本地化）再 Build

### Lua 逻辑热更（xLua）
- **边界纪律**：Lua 只写运营外围（公告/活动/UI 流程——改动最频繁的逻辑）；
  **战斗核心永不 Lua 化**——确定性逐位一致、回滚性能、浮点行为三条红线。
  "哪些代码该热更"的边界划分本身就是架构决策。
- **链路**：`require "notice"` → `LuaService` loader → Addressables `Lua/notice`
  （文件 `Assets/LuaScripts/notice.lua.txt`）→ 与资源/数据共用同一条 remote catalog 热更管线。
- **示例模块**：主菜单公告窗——`NoticeView` 是壳，显示开关/标题/正文/关闭回调全在
  `notice.lua.txt`；改 Lua → Update a Previous Build → 重启即生效（三位一体热更：资源/数据/逻辑）。
- **运行模式**：反射模式（编辑器/Mono 出包够用）；IL2CPP 出包前需 `XLua/Generate Code`
  （委托桥如 `Get<Action>` 要配 CSharpCallLua），且生成的 Gen 目录须移出 `XLua.Runtime`
  程序集（它引用游戏类型，会与 FTG.Runtime 成环）。
- **asmdef 注意**：程序集名是 `XLua.Runtime` 而非 `XLua`——托管 `XLua.dll` 会与
  原生插件 `xlua.dll` 同名冲突（Windows 不分大小写），Unity 直接拒绝。

### 调手感
- 移动速度/跳跃/冲刺 → `MovementConfig`；预输入窗口 → `CommandQueue.BufferFrames`
- 帧数据（发生/持续/恢复/判定框）→ `Assets/BoxData/*.json`（可热更）

---

## 七、待办（按优先级）

1. **伤害递减（damage scaling）**——连段每下全额伤害会让游戏瞬间失衡
2. **受击姿态细分**——站立/蹲姿/浮空/倒地各档受击
3. **定点数迁移**——上 Go 服务器前必须做（float 跨语言不保证逐位一致）
4. 气槽 / 超必杀 / 飞行道具；训练模式 + 帧数据工具；Timeline 演出
