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

## UI 合批与重建

UGUI 的帧成本大头不是渲染而是 **Canvas 重建**：任何一个 Graphic 变脏（改字、改 fillAmount），
它所在 Canvas 的全部网格重新合批。策略是控制重建的【频率】与【范围】：

| 措施 | 原理 | 位置 |
|---|---|---|
| VM 字符串按变化格式化 | `Set` 只去重通知拦不住 `ToString` 分配；改后计时/胜场/连击文本从 60 次/秒降到事件级 | `BattleHudViewModel.Refresh` |
| 动静分离（嵌套 Canvas） | 计时/连击/播报/血条填充各套一层子 Canvas，脏了只重建自己；代价是各多 1 个 draw call（HUD 元素个位数，划算） | `BattleHudView.IsolateDynamic` |
| 层级 Canvas 天然隔离 | UIManager 每层独立 Canvas：HUD 每秒跳字不会牵连菜单/弹窗层重建 | `UIManager.Awake` |
| 关闭无用射线 | Hud/Toast 层 GraphicRaycaster 整层关闭 + HUD 全部 Graphic `raycastTarget=false`，指针事件不再逐帧遍历 | `UIManager` · `BattleHudView.OnOpened` |

**验证方法**：
- Frame Debugger：战斗中 batch 数应稳定；连击跳数时只有连击文本所在小 Canvas 的批次变化。
- Profiler → UI / UI Details 模块：`Canvas.BuildBatch` 与 Layout 只在秒跳/命中时出现，且耗时**微秒级**。
- Game 视图 Stats：对局中 Batches 波动 ≤ 个位数。

## 实测数字·UI（截图后填写）

| 场景 | Canvas.BuildBatch 触发频率 | Batches | 备注 |
|---|---|---|---|
| 中立对峙（仅计时跳秒） | 待测（目标：~1 次/秒） | | |
| 连招进行中 | 待测（目标：仅命中帧） | | |

## 后续（M4 可选补充）

- ~~Playables API 替换 `Animator.Play` 硬同步~~ ✅ 已完成（`FighterAnimationPlayer`，Manual 模式直驱）。
- 渲染统计（SetPass/Batches）与构建体积前后对比（Addressables 分包后的包体收益）——数字随出包补录。
