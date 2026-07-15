# 格斗游戏核心框架

Unity 2022.3.61f3 · Loxodon Framework (MVVM) · Unity Input System

---

## 一、分层架构

```
┌─ App ────────────────────────────────────────────────┐
│  GameLauncher            应用组合根（全局服务注册）      │
└──────────────────────────────────────────────────────┘
┌─ Core（纯 C#，无 Unity-UI 依赖，可单测/可回滚） ────────┐
│  InputTypes    按键掩码、单帧输入快照、数字键盘记法       │
│  InputBuffer   环形缓冲（120 帧历史，零 GC）            │
│  MotionPattern 搓招模式定义 + 常用招式库                │
│  MotionDetector 回溯匹配、蓄力、镜像、优先级吞并         │
│  CommandQueue  指令队列（预输入缓冲，手感来源）          │
│  InputQuery    帧精确回看（拒止/拆投/读对方意图）        │
│  FightingInputController  60Hz 采样总控                │
└──────────────────────────────────────────────────────┘
┌─ Battle（战斗核心，纯逻辑，位置/判定的唯一权威） ────────┐
│  BattleLoop         中央时钟，双方严格同帧推进           │
│  FighterState       角色状态机（招式 + 移动统一调度）     │
│  MoveData           帧数据（发生/持续/恢复/位移）        │
│  MoveTable          指令 → 招式解析层（连招规则）        │
│  BoxData            判定框数据模型（关键帧插值）         │
│  BoxDataLoader      JSON 加载（跨语言，Go 服务器共用）    │
│  CollisionResolver  攻防裁决管线（反制系统的集中地）      │
│  MovementController 移动状态机（走/跑/冲刺/跳/后跃）      │
│  MovementConfig     移动参数（手感调优单一入口）         │
│  PushboxResolver    推挡解算（防重叠 + 版边传导）        │
│  FighterDefinition  角色数据仓库（数据唯一出处）         │
│  BattleBootstrap    战斗组合根                         │
│  BattleServiceBundle / BattleMessages                 │
│  FighterView        表现层傀儡（动画只是显示器）         │
└──────────────────────────────────────────────────────┘
┌─ UI（Loxodon MVVM，对核心零引用） ────────────────────┐
│  BattleHudViewModel / View      战斗 HUD              │
│  InputHistoryViewModel / View   训练模式输入显示        │
└──────────────────────────────────────────────────────┘
┌─ Editor（工具链） ───────────────────────────────────┐
│  HitboxEditor             判定框可视化编辑（核心工具）    │
│  BatchRootMotionBaker     批量位移烘焙 → 静态数据类      │
│  RootMotionBaker          单个位移烘焙（含曲线预览）      │
│  AnimatorContractValidator  AC 契约校验                │
│  FrameDataDiagnostics     帧数据 vs Clip 时长诊断       │
└──────────────────────────────────────────────────────┘
```

---

## 二、每帧执行顺序（BattleLoop.Tick）

```
① 朝向同步       用位置关系决定朝向（搓招镜像依赖它）
② 输入采样       双方同帧采样 → 搓招检测 → 指令入队
③ 状态推进       招式状态机 → 移动状态机（顺序不能反）
④ 推挡解算       防重叠 + 版边约束（位置先解算干净）
⑤ 攻防裁决       无敌 → 当身 → 投/拆投 → 拒止 → 防御 → 命中/CH
⑥ 事件广播       HitEvent 发布到战斗 Messenger（表现层订阅）
```

---

## 三、核心设计原则

### 1. 位置权威归逻辑，动画只是显示器
`Animator` 的 **Apply Root Motion 必须取消勾选**。位移是 `MoveData.RootMotion` 里的
帧数据，由 `FighterState` 结算。动画时钟按渲染帧走，逻辑按 60Hz 走，让 Animator
驱动位置会直接毁掉帧确定性，且回滚网络无法实现。

### 2. 两个 ID 不是一回事
- `MotionPattern.Id` = **指令名**（玩家搓了 236+拳）
- `MoveData.MoveId` = **招式名** = Animator State 名 = Clip 名

中间由 `MoveTable` 解析：同一指令按键强度/姿态/取消来源 → 不同招式。

### 3. 反制系统的三层信息源
| 层 | 用途 | 代码位置 |
|---|---|---|
| 状态层（帧数据） | Counter Hit、确反、克制 | `FighterState.Phase` / `MoveFrame` |
| 碰撞层（裁决管线） | 所有判定的锚点 | `CollisionResolver.Judge()` |
| 输入层（回看缓冲） | 拒止、拆投、AI 假人 | `InputQuery` |

**90% 的反制不需要读按键**——读对方"正在出什么招、第几帧"就够了。
按键 ≠ 行动（硬直中的按键不产生行为）。

### 4. Messenger 单向红线
Messenger 只做「核心 → 表现层」的广播（已发生事实的通知）。
任何**改动战斗状态**的逻辑（AI、假人）必须挂 `BattleLoop.TickFinished`
这个 C# 事件，保证帧内顺序确定——这是回滚网络的前提。

### 5. 组合根原则
只有 `GameLauncher`、`BattleBootstrap`、View 脚本能接触服务容器。
其余业务类一律构造注入，绝不调用 `Context.GetApplicationContext()` 定位服务
（否则容器退化成另一种全局单例）。

### 6. 数据格式：JSON 而非 ScriptableObject
Go 服务器要跑权威模拟，必须读同一份帧数据。SO 是 Unity 私有格式，Go 读不了。
判定框直接落地 JSON，Unity 与 Go 共用一份真相。

### 7. 座位与角色分离（接线的核心原则）
**输入属于"座位"，不属于"角色"**：P1 用 WASD、P2 用方向键，这个绑定在选人之前
就存在，与选了谁无关。因此：
- `FightingInputController` = 座位设备，**场景常驻对象**（P1Seat / P2Seat），
  绝不挂在角色 Prefab 上——那等于"角色自带键位"，双方选同一角色时会共用按键
- 角色 Prefab 只带表现（模型 + Animator + FighterView），**零场景引用**
  （Prefab 资产无法序列化场景对象引用，挂了也拖不进去）
- `BattleBootstrap` 是**唯一的角色实例化点**：运行时 Instantiate 角色 Prefab，
  经 `FighterView.Bind` 把座位输入、角色数据、表现视图组装起来
- `BattleLoop` **没有任何 SerializeField**，依赖全部经 `Initialize` 注入
  （避免与 Bootstrap 重复持有同一引用——同一对象拖两遍是接线错误的温床）

---

## 四、场景接线

编辑期场景里只放这些对象：

| 对象 | 组件 | 说明 |
|---|---|---|
| GameLauncher | GameLauncher | 全局服务（DontDestroyOnLoad） |
| BattleLoop | BattleLoop | 中央时钟，**无需拖任何引用** |
| P1Seat | FightingInputController | 绑 P1 的 InputActionReference |
| P2Seat | FightingInputController | 绑 P2 的 InputActionReference |
| BattleBootstrap | BattleBootstrap | 拖入 loop、两个 Seat、两个角色 Prefab |
| Canvas/BattleHud | BattleHudView | 拖入 bootstrap 和两个 Text |

角色 Prefab 检查清单：
- 有 Animator（Apply Root Motion 若忘了关，FighterView.Bind 会强制关闭并告警）
- 有 FighterView（朝向模式：3D 蒙皮模型用 RotateY——负缩放会翻转蒙皮法线；2D 用 MirrorScaleX）
- **没有** FightingInputController（输入属于座位）
- **没有**任何指向场景对象的 SerializeField

输入资产注意：P1Seat 和 P2Seat 必须绑**不同的 Action**（Input Action Asset 里建
P1 / P2 两个 Action Map，各自绑键盘不同区域或不同手柄），否则两个座位读同一套按键。

---

## 五、AnimatorController 契约

AC 退化成**平铺的状态仓库**：**不连任何 Transition、不用 Parameter**。
（你已经有一个真正的状态机 `FighterState` 了，Mecanim 再连一套会打架——
它的过渡时长、exit time 会吃掉打断，而格斗游戏"第 8 帧被打断就是第 8 帧"没有商量。）

**必需的 State**（全部平铺在 Base Layer 顶层）：
- 每个招式一个，名字 = `MoveData.MoveId`
- 松散状态：`Idle` `Hitstun` `Blockstun`
- 移动状态：`WalkForward` `WalkBackward` `Dash` `BackDash`
- 跳跃：三个 clip（垂直/前/后），名字在 `MovementConfig.JumpNeutral/Forward/Backward.ClipId` 里配。
  每个是【一整段完整动画】（起跳预备→腾空→落地）。
  **跳跃是帧数据驱动的，不是物理模拟**——抛物线用 `FG/Batch Root Motion Baker`
  从跳跃 clip 烘成 `JumpData.RootMotion`，与升龙(623P)走同一套机制。
  腾空时间 = clip 长度，落地 = 帧数走完（没有 velocity / gravity / 落地检测）。
- 跑步：没有跑步循环动画时，把 `MovementConfig.EnableRunning` 关掉（默认已关），
  即为冲刺制（街霸四代/五代式，现代主流）。`runState` 留空会自动回退到冲刺动画。

**Clip 要求**：60fps 采样；**帧数 = 帧数据的 TotalFrames**（这样动画第 N 帧 == 逻辑第 N 帧）；
招式 Clip 不勾 Loop Time。

跑 `FG/Animator Contract Validator` 一键校验全部契约。

---

## 六、工作流

### 加一个新招
1. `FighterDefinition` 里加 `MoveEntry`（指令/按键/姿态/连招规则）
2. 同处加 `MoveData`（发生/持续/恢复/伤害/硬直）
3. AC 里拖入 Clip，State 改名为 MoveId
4. `FG/Hitbox Editor` 画判定框 → 保存 JSON 到 `Resources/BoxData/`
5. 跑 `FG/Animator Contract Validator` 校验

### 位移数据（单一数据源：JSON）
所有位移都烘进 `Resources/BoxData/{角色}_boxes.json`，运行时由 `BoxDataLoader` 注入。
- **批量**：`FG/Batch Root Motion Baker` —— 新角色接入时一口气烘完所有 clip
- **单个**：`FG/Hitbox Editor` 里的「烘焙位移」—— 美术改了某个动画后重烘
- 前进轴选 **Z**（3D 角色的 Unity 标准前向；看 Clip 的 Average Velocity 哪维非零）
- 两个工具写同一份 JSON，合并式写入：重烘位移不会毁掉已画的判定框

⚠ **顺序：先烘位移，再画判定框。**
判定框的坐标相对【逻辑原点】，而逻辑原点 = 烘焙位移的累加。
没烘位移时逻辑原点恒为零点，对带位移的招式画框会把动画漂移吃进坐标里，
游戏中判定框会偏到角色前/后方（"幽灵框"）。

### 调手感
- 移动速度/跳跃高度/冲刺距离 → `MovementConfig`
- 预输入窗口 → `CommandQueue.BufferFrames`（默认 8）
- 拒止/拆投窗口 → `CollisionResolver`

---

## 七、待办（按优先级）

1. **伤害递减（damage scaling）** —— 连段每下全额伤害会让游戏瞬间失衡
2. **受击姿态细分** —— 现在 Hitstun 只有一种，实际需要站立/蹲姿/浮空/倒地
3. **定点数迁移** —— 上 Go 服务器前必须做（float 跨语言不保证逐位一致）
4. **帧数据导出 JSON** —— 让 Go 服务器能读（仓库接口已就位，换实现即可）
5. 气槽 / 超必杀 / 飞行道具
