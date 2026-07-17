# 性能优化记录（M4）

用数字说话：每项优化都要有 **修复前/后的 Profiler 实测**，没有数字的优化不合入。

## 测量方法

- Unity Profiler → **CPU Usage** 模块 → Hierarchy 视图，定位 `BattleLoop.Update()` 行，看 **GC Alloc** 与 **Time ms** 两列。
- 三种采样场景：**中立对峙**（双方站立）、**连招进行中**（gatling 连段）、**命中瞬间**（含顿帧/火花/HUD 刷新）。
- Deep Profile 只用于定位调用栈，**计数以普通模式为准**（Deep Profile 本身有巨大开销）。
- Editor 下 Loxodon 绑定、Debug.Log、Inspector 刷新会带来额外分配；对外报告的数字以 **Development Build 播放器**为准。

## 战斗循环 0GC——分配点审计（代码级）

| 位置 | 频率 | 处理 |
|---|---|---|
| `FighterState.TryAct` 谓词 lambda ×2（捕获 `cancelSource`/`cmd`） | **每逻辑帧**（中立态探测 + 出招中取消判定，60 次/秒起） | ✅ **已修复**：谓词提为构造期缓存字段，参数经 `pending*` 字段传递，零每帧分配 |
| `CollisionResolver` 的 `HitEvent` | 每次命中/拼招（事件级，非每帧） | 保留：低频；池化的复杂度收益比不划算 |
| `CommandQueue.Enqueue` / 检测事件的 `DetectedCommand` | 每次搓招检测成功（输入事件级） | 保留：同上 |
| `RoundResult` | 每回合一次 | 保留 |
| HUD 字符串（计时器每秒、连击数变化时） | 秒级/事件级 | 保留：表现层，不在战斗循环内 |
| `ReplayRecorder` 帧列表扩容 | List 摊销增长 | 保留：6 字节结构体，增长次数对数级 |

**目标态**：中立对峙与连招进行中，`BattleLoop.Update` 的 GC Alloc = **0 B**；
命中帧允许一次小分配（HitEvent + HUD 连击文本），这是事件级而非帧级成本。

## 明确不做的优化（及理由）

- **MoveId string → int 哈希**：C# 字符串字典查找不产生分配、比较不装箱——迁 int 只省
  微量 CPU，却破坏「MoveId = JSON 键 = Animator State 名 = Clip 名」的数据驱动一致性，
  调试/热更/编辑器工具全要加一层反查。数据一致性价值 > 微优化。
- **Jobs/Burst**：当前数据量（2 角色、每帧十几个盒）撑不起并行收益；
  等 Phase 4 回滚网络需要一帧内重模拟 N 帧时才有真实动机。
- **HitEvent/DetectedCommand 池化**：事件级频率（次/秒 << 60），池的生命周期管理复杂度
  高于收益；若 Profiler 显示连招高峰有可感知尖峰再回头做。

## 实测数字（Profiler 截图后填写）

| 场景 | 修复前 GC Alloc/帧 | 修复后 GC Alloc/帧 | Time ms（参考） |
|---|---|---|---|
| 中立对峙 | 待测 | 待测（目标 0 B） | |
| 连招进行中 | 待测 | 待测（目标 0 B） | |
| 命中瞬间 | 待测 | 待测（允许事件级小分配） | |

> 验证路径：Play → Profiler CPU → 搜索 `BattleLoop.Update` → 分别在三种场景各停留数秒读数。
> 截图存 `docs/img/`，贴进上表。

## 后续（M4 剩余）

- Playables API 替换 `Animator.Play` 硬同步（动画深度控制：混合、变速、逐帧驱动）。
- 渲染统计（SetPass/Batches）与构建体积前后对比（Addressables 分包后的包体收益）。
