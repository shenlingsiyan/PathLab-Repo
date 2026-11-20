# 指向跳板（Directional Launcher）

地图设施，**不是寻路算法**。它占据一个**仍然可通行**的格子：单位随时可以像
普通地形一样走进去、走出去；只有当单位**主动选择**时，才沿固定方向直接转移到
固定落点。落点如果正好是另一个启用中的跳板，就必须继续连锁，直到落在普通格子。

```text
U 站这里                     P 跳板（可走）
[U] → [P] ══×3══> [T]        主动使用：U → T，一步
                  ↑
            中途的墙/空地都不检查
```

代码：`PathLab/Core/DirectionalLauncherFacility.cs`（设施 + 连锁解析 + 探针）、
`PathLab/Core/LauncherPathPlan.cs`（路径事件回放）。

## 1 数据

```csharp
public sealed class DirectionalLauncher
{
    public int Id { get; }                    // 设施内唯一，从 1 开始
    public Cell Position { get; }
    public Direction Direction { get; internal set; }        // 复用单向通道的方向枚举
    public int TransferDistance { get; internal set; }       // 正整数，可修改
    public bool Enabled { get; internal set; } = true;
    public Cell Target { get; }               // Position + DirectionVector * TransferDistance
}
```

复用现有 `Direction`（`Up / Down / Left / Right`）与 `Directions.Step`，
Y 轴向下：`Up = (0,-1)`、`Down = (0,1)`、`Left = (-1,0)`、`Right = (1,0)`。

```csharp
DirectionalLauncherFacility:
    IReadOnlyList<DirectionalLauncher> Launchers
    bool Add(GridMap, Cell, Direction, int distance, out string? note)
    bool RemoveAt(Cell)
    void Reset()
    DirectionalLauncher? At(Cell)
    bool IsLauncher(Cell)
    bool SetDirection(int id, Direction)
    bool SetDistance(int id, int distance)   // < 1 直接拒绝
    bool SetEnabled(int id, bool enabled)
    LauncherChainResult ResolveChain(GridMap, DirectionalLauncher)
    LauncherChainResult ResolveChain(GridMap, Cell)
    IReadOnlyList<SpecialTransition> TransitionsFrom(GridMap, Cell)
    IPathProbe ApplyTo(IPathProbe inner, GridMap map)
    IPathProbe CreateProbe(GridMap)
```

放置只接受地图内、非起点/终点、空格子；**不涂改地图**，跳板格的地形代价、
墙规则、起点/终点照旧。`TransferDistance` 必须是正整数，越界或为 0 的放置被拒绝。

## 2 普通穿越：跳板格不是障碍

跳板格完全保留普通 4 邻接：单位可以从任意合法相邻方向进入，也可以从任意
合法方向离开，**不触发转移**。因此 `NormalNeighbours` 不会因为跳板而删边，
跳板也不会被当成墙。

## 3 主动使用：一条独立的候选边

寻路扩展一个格子时，除了普通邻居，还会拿到该格上可选的跳板移动：

```text
单位站在 U（跳板 P 的邻格，或就是 P 本身）
    ↓ 主动选择
从 P 开始解析连锁
    ↓
最终普通落点 T
```

- 从**相邻格**就可以使用，不要求先普通移动踩上跳板；这正是「两者同时可选」的来源：
  同一格上的普通移动候选（走进 P）和特殊移动候选（U → T）并列存在；
- 站在跳板格上也可以主动触发，属于同一条规则的补充，不是前置要求；
- 设施只发布候选，选不选、按什么代价算，全部由寻路算法决定；
- 失败连锁（越界/落点不可行/循环）不会出现在候选里，但普通穿越永远保留。

## 4 转移与连锁

单个跳板 P：方向 D、距离 L：

```text
Target = P.Position + D * L
```

**只检查落点，不检查中途**。起点与目标之间隔了多少墙、多少其他跳板都不影响这次
转移；中途经过的跳板也不会被触发（只有「落点恰好是启用跳板」才连锁）。

链表执行：

```text
主动选 A
A 落点 B（启用跳板）  → 强制 B
B 落点 C（启用跳板）  → 强制 C
C 落点 D（普通格）    → 连锁结束，D 是唯一最终落点
```

- 第一次选择之后不允许中途退出，也不允许停在中间跳板上；
- `ResolveChain` 是独立模块：只计算并验证结果，不做寻路决策；
- 每次解析都新建 `HashSet<int> visitedLauncherIds`，重复触发同一 ID 即判循环，
  **不使用固定次数上限**；循环只废掉这条特殊移动，不影响普通通行。

```csharp
public sealed class LauncherChainResult
{
    bool Success;
    Cell? FinalCell;                     // 失败时为 null
    IReadOnlyList<int> TriggeredLauncherIds;   // 按触发顺序，含主动选择的第一个
    IReadOnlyList<Cell> LandingPositions;      // 每次触发后的落点
    int GeometricDistance;               // 各次 TransferDistance 之和，纯几何
    LauncherChainError Error;            // None / Disabled / OutOfBounds / Blocked / Cycle
    string? Note;
}
```

### 4.1 落点合法性

只检查每个跳板的**实际落点**：

| 落点 | 行为 |
|---|---|
| 地图外 | 整条连锁失败（`OutOfBounds`），不产生非法坐标 |
| 启用的跳板 | 强制继续，不能停 |
| 禁用的跳板 / 普通格 | 作为终点；检查该格是否可通行 |
| 墙或其他不可通行格 | 整条连锁失败（`Blocked`） |

中途经过的格子不查通行性。禁用跳板不会被当成连锁节点：它的格子如果是空地，
就是合法终点；如果是墙，就按墙处理并让整条连锁失败。

## 5 寻路接入

跳板不绑定任何算法。`IPathProbe` 的既有两类接口保持不变：

```csharp
GetNormalNeighbours(cell)      // 普通 4 邻接（跳板格照常可走）
GetSpecialTransitions(cell)    // 该格可选的跳板移动（可能是一条完整连锁）
```

`SpecialTransition` 新增三个可选字段（传送门调用点无需改动）：

```csharp
public readonly record struct SpecialTransition(
    Cell Destination, TransitionType Type, int Cost,
    int SourcePortalId = 0, int DestinationPortalId = 0,
    IReadOnlyList<int>? TriggeredLauncherIds = null,
    IReadOnlyList<Cell>? LandingPositions = null);
```

`Type = TransitionType.Launcher`。`Cost` 固定为 **0**：设施不收取任何费用，
也**不把几何距离折算成代价**。算法各自定价：

| 模块 | 一次跳板移动的代价 |
|---|---|
| `00_shortest_steps` | 1 步（不管转移距离多长）|
| `01_min_cost` | `0 + CostAt(落点)`，与普通移动“进入格子付地形费”一致 |
| `03` / `05` | 沿用其内部使用的 00/01 口径 |
| `06` | 1 步、权重 0（跳板没有方向贡献，与传送一致）|
| `07` | 1 步，穿越数按落点计 |

BFS 只有在所有边权重相同时才保证最短；若后续给跳板设定非统一代价，应改用
Dijkstra。仓库中目前没有 A*；新增 A* 时必须证明启发式在“远距离低成本跳板”
下仍可采纳，否则以 h=0 的 Dijkstra 作为正确性基准。本实现**不改任何算法目标函数**。

探针可组合。界面里的组合顺序：

```csharp
aware.Probe = _launchers.ApplyTo(_oneWay.ApplyTo(_portals.CreateProbe(_map)), _map);
```

- 传送门/单向通道的普通邻居与特殊移动原样保留，跳板只追加自己的候选；
- `IsOneWay` / `ExitsAfter` 直接转发给内层探针，单向通道规则不受影响；
- `For(clone)` 逐层重绑地图，`03` / `05` 的克隆图看到同一批跳板。

## 6 路径结果与运行时

坐标路径本身不足以回放连锁（一次跳板只留下“远距离一步”）。因此：

```csharp
public readonly record struct LauncherJump(
    Cell From, Cell Destination,
    IReadOnlyList<int> TriggeredLauncherIds,
    IReadOnlyList<Cell> LandingPositions);
```

- `SearchResult.LauncherJumps` 保存本次路径实际用到的跳板事件；
- 载体在每次 `Plan` 后用 `LauncherPathPlan.Attach(map, probe, result)` 填充；
  纯坐标路径与普通节点含义不变；
- `LauncherPathPlan.Build` 只读探针再匹配一遍非 4 邻接的步：命中
  `TransitionType.Launcher` 就记事件，其他设施（如传送门）的步直接跳过，
  无法解释的步把 `Resolved` 置 false。它不改搜索、不改代价、不改路径节点；
- 两个跳板都从同一格落到同一格时，按放置顺序取第一条，结果可复现；
- 运行时按 `LauncherJumps` 逐次触发 Pad、逐次落到 `LandingPositions`，
  再继续普通路径节点即可。

## 7 动态更新

设施不缓存任何连锁或候选，**每次查询都按当前地图与当前跳板属性重算**：

- 增删跳板、改方向、改距离、启用/禁用，下一次 `TransitionsFrom` 立即生效；
- 改落点通行状态（涂墙/拆墙）同样立即生效；
- 连锁中任意一个跳板被修改，下一次解析立刻反映；
- 首版以正确性优先，没有戳/缓存，也不存在失效遗漏。

## 8 边界情况

| 情况 | 行为 |
|---|---|
| 跳板贴地图边界 | 允许放置；落点越界则特殊移动不可用 |
| 跳板格被涂成墙 | 墙优先：格子不可通行，该跳板的候选消失；拆墙后恢复 |
| 落点就是触发格本身 | 零位移候选被丢弃 |
| 落点是禁用跳板且为墙 | 按墙处理，整条连锁失败 |
| 一条连锁中多个跳板 | 只有第一个由算法选择，其余强制 |
| 循环 A→B→C→A | `Cycle`，整条不可用 |
| 多个跳板相邻 | 每格可同时拿到多条候选，由算法选择 |
| 起点/终点 | 不能放置跳板 |
| 同格重复放置 | 拒绝 |
| 换新地图 | `Reset()` 清空所有跳板（不写入地图，无需还原）|

## 9 界面

- 编辑行新增 `跳板` 按钮、`方向`（↑ ↓ ← →）与 `距离`（1..15，默认 3）；
- 左键放置、右键移除，与其他设施同一套手势；**同一时刻只有一个工具生效**；
- 画布把跳板画成**绿色箭头 + 左上角转移距离数字**：橙色仍是单向通道，
  绿色表示“可选跳板”；禁用的跳板画成灰色；
- 状态栏在有跳板事件时追加 `· 跳板 N 次`；传送门次数单独统计；
- 换新地图会清空所有跳板；放/删/改地图后立即重算。

## 10 测试（`--selftest`，launcher 系列）

| 组 | 覆盖 |
|---|---|
| launcher 1 | 上/下/左/右目标公式；距离可修改；非正整数被拒绝 |
| launcher 2 | 四邻接普通穿越；该走就走、不产生跳板事件 |
| launcher 3 | 邻格主动使用、落点准确、事件带 ID 与落点；落点变墙/拆墙即时生效；跳板格自身也可触发 |
| launcher 4 | 中途三格全墙，转移成功；BFS 一步跨越 |
| launcher 5 | 最终落点墙 → 整条不可用；普通穿越仍合法 |
| launcher 6 | A→B→C→普通格；强制触发 B、C；BFS 得到最终落点 |
| launcher 7 | 第三个跳板落点墙 → 整条连锁不可用，不提供半截移动 |
| launcher 8 | A→B→C→D→A 循环被识别；不进入候选；普通通行不受影响 |
| launcher 9 | 禁用跳板按普通格处理（空地成功、墙失败）；重新启用恢复强制连锁 |
| launcher 10 | 落点越界；不返回非法坐标；所有候选落点均在地图内且非墙 |
| launcher 11 | BFS/Dijkstra 可自由选择普通或跳板；无收益时保持原路径；全算法扫描逐对校验 |
| launcher 12 | `SearchResult.LauncherJumps` 完整（ID + 每次落点），重放后停在同一点；整条路线审计通过 |
| launcher rules | 越界/闸门/零距离/重复放置拒绝、移除、`Reset`、`For(clone)` 重绑 |

路径审计 helper：`SelfTest.LauncherPathIsLegal(map, facility, path)`——
每步要么是合法 4 邻接，要么必须能在设施当前发布候选里找到同落点的
`TransitionType.Launcher` 边；只查落点，不查中途格。

## 11 约束兑现

- 不改地图随机生成机制（RMG / 内置生成器零改动）；
- 不改普通障碍定义，不把跳板当不可通行格；
- 不强制使用跳板，不强制连锁中途退出，不允许半截连锁；
- 中途障碍不阻止转移，最终落点不可通行则整条不可用；
- 循环检测用独立访问集合，不用固定次数；
- 不改任何算法的目标函数，规则只存在于设施/探针层；
- 无冷却、次数、耐久，无随机落点、可变方向或非直线转移。

## 12 极简

一个设施类 + 一个连锁结果 + 一个事件回放类；复用 `Direction`/`Directions`/
`Cell`/`GridMap`/`IPathProbe`/`SpecialTransition`；没有新寻路算法、没有地图
类型改动、没有缓存与失效逻辑；界面只加了一个模式按钮、一个方向框和一个数字框。
