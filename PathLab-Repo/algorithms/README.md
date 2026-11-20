# algorithms — 寻路算法源目录

每个算法一个文件夹，**源文件只在这里维护**。可视化载体 `PathLab` 使用
`tools\sync-algorithms.ps1` 把各文件夹拷贝进 `PathLab\Algorithms\` 后编译调用。

目录里有两类模块：

- **路径模块**：实现 `IPathfinder`，返回 `Start → Goal` 的路径，出现在下拉框并参与对比运行；
- **查询模块**：实现 `IFarthestPointFinder`、`ICandidateTargetFinder` 等查询契约，
  只回答一个问题（如最远可达格子、半径候选目标点），不产生路径，因此不出现在下拉框。

## 目录约定

```text
algorithms/
  00_shortest_steps/
    ShortestStepsPathfinder.cs   唯一实现（可多个 .cs，但只允许一个 IPathfinder）
    README.md                    名称、原理、代价口径、复杂度、输入输出
  01_min_cost/
    MinCostPathfinder.cs
    README.md
  02_farthest_cell/
    FarthestFromTargetFinder.cs  查询模块（实现 IFarthestPointFinder，不是路径模块）
    README.md
  ...
```

- 文件夹：`NN_短名`，`NN` 决定下拉框顺序。
- 类：唯一类名，必须有无参构造函数；路径模块实现 `PathLab.Core.IPathfinder`，
  查询模块实现 `PathLab.Core.IFarthestPointFinder`。
- 命名空间：`PathLab.Algorithms.<短名>`。
- 只依赖 `PathLab.Core`（`GridMap` / `Cell` / `SearchResult` / `IPathfinder` /
  `IFarthestPointFinder` / `FarthestPointResult`），不引用 WinForms，不读写外部文件。
- 每个文件夹必须有 `README.md`，至少写清：名称、算法、代价口径、复杂度、输入输出。

## 契约

```csharp
string Id { get; }                 // 排序键，建议与文件夹名一致：00-shortest-steps
string Name { get; }               // 下拉框显示名
string Summary { get; }            // 一句话说明
SearchObjective Objective { get; } // Steps（步数）、Cost（进入代价）或 Score（加权得分）
bool Optimal { get; }              // 是否声明对 Objective 最优
bool Constrained => false;         // 是否在额外约束下最优（默认 false）
SearchResult Search(GridMap map)
```

- 移动：4 邻接；进入格子付 `map.CostAt(cell)`；0 = 墙，不可进入。
- 起点代价不计，终点代价计入。
- 输出 `Path` 必须从 `map.Start` 到 `map.Goal`。
- `Cost` 始终填**该路径的实际进入代价之和**，即使算法优化的是步数。
- `Explored` 按出队/访问顺序记录，供可视化。
- 若路线受「必须/尽量少经过某些格子」约束，可填 `SearchResult.ReferenceCount`（实际穿越次数），
  并从最终路径独立重算（`07_min_reference_traversal` 是示例）；其它模块留 0。
- 若声明 `Optimal = true`，`--selftest` 会按 `Objective` 选择独立对照：
  - `Cost` → O(V²) Dijkstra 校验总代价；
  - `Steps` → BFS 校验步数；
  - `Score` → 不使用通用对照（通用循环里打印 SKIP），由模块自己的独立 oracle 校验。
- 若同时声明 `Constrained = true`（如 `05_obstacle_cluster_route`），该算法的代价可以合理地
  **高于无约束最优**，`--selftest` 跳过上面的普通最优对照，改用模块自己的约束 oracle。
- 模块**不需要**关心单跑还是对比运行：载体为每个机器人创建一个独立实例
  （`AlgorithmHost.Create`），并同时推进各机器人；对比模式只实例化 A/B 两个算法。
- 因此模块可以保留与自身实例相关的状态（如增量重规划缓存），
  但不要依赖跨实例共享的全局可变状态。

### 得分型模块（`SearchObjective.Score`）

得分不是步数也不是代价，`SearchResult` 为此新增三个字段（步数/代价模块留空即可，默认 0/null）：

```csharp
double Score { get; set; }             // 目标函数值，越大越好
bool IsOptimal { get; set; }           // 本次结果是否已证明最优（按单次结果判定）
string? TerminationReason { get; set; } // 结束原因：找到精确最优 / 近似 / 超时 / 节点上限 / 不存在合法路径 …
```

- 得分模块的 `Optimal` 通常声明为 `false`（是否最优由每次结果的 `IsOptimal` 决定），
  这样 `--selftest` 的通用最优对照不会误判；
- 必须提供**独立**的评分函数（不经由搜索过程的累加器），`--selftest` 用它重算并比对；
- 近似搜索绝不能置 `IsOptimal = true`；预算耗尽时应返回已有的最好合法路线并说明原因，
  一条完整路线都没有时返回 `Found = false`。

## 查询型模块契约

```csharp
string Id { get; }                                   // 排序键
string Name { get; }                                 // 显示名
string Summary { get; }                              // 一句话说明
FarthestPointResult Find(GridMap map, Cell target)   // 参考点由调用方给出
```

- 返回 `Found` / `Cell` / `Distance` / `Candidates` / `Distances`（`int[x, y]`，`-1` = 墙或不可达） / `Note`。
- 距离口径由模块自己的 `README.md` 定义；`02_farthest_cell` 用「4 邻接、不进墙、忽略地形代价的步数」。
- 并列结果必须有确定性规则，并把全部并列项放进 `Candidates`。
- 查询模块**不放** `IPathfinder` 实现：它没有 `Start → Goal` 路径，
  `--selftest` 的路径校验、下拉框与对比运行都不适用于它。载体目前只把它编译进来供调用方使用。
- 另一种查询契约是 `ICandidateTargetFinder`（`04_radius_candidates`）：
  `Find(GridMap map, Cell entrance, double alpha, int? minRadius, int? maxRadius)`，返回
  `Success` / `Error` / `BasePathSteps` / `Alpha` / `Radius` / `MinRadius` / `MaxRadius` /
  `IsRing` / `Candidates` / `CandidateCount` / `Note`。
  区间两端都可缺省（缺省取 `R`）；区间为负或下界大于上界返回 `InvalidRadiusRange`，不交换不夹取。
  它允许“成功但 0 个候选”，调用方不得把空结果当成失败。

## 需要参考点的路径模块

想在界面上让用户指定一个「参考点」，路径模块额外实现 `PathLab.Core.IReferencePointModule`：

```csharp
Cell? ReferencePoint { get; set; }  // 界面写入；null 表示用模块自己的默认值
Cell? UsedReference { get; }        // 上次搜索实际使用的参考点，供状态栏显示
Cell? FarthestPoint { get; }        // 上次搜索得到的最远点，供画 F 标记与虚线返程
```

- 载体在创建机器人后把当前唯一的参考点写入每个实现了该接口的模块（`ApplyReferencePoint`）；
- 只有当前机器人里存在这类模块时，界面才显示 `参考点` 一行与 `选参考点` 按钮；
- 用户没点过参考点时，载体给一个默认值：普通模块取入怪口，要求障碍的模块
  （`05_obstacle_cluster_route`，见下）取**行优先第一个墙格**；用户一旦点选过，就不再自动覆盖；
- 模块必须自己校验参考点（越界 → 失败并给出 `Note`），不要静默替换；参考点可以是空地或障碍，
  障碍参考点的距离口径由模块定义（`03_farthest_loiter` 经 `02` 从其可通行邻格起步；
  `05_obstacle_cluster_route` 则要求参考点必须是墙，并取整块连通障碍群）；
- `03_farthest_loiter` 是示例：`FarthestPoint` 也用来把路径切成去程实线、返程虚线。

需要一个**参考点集合**（一个格子不够用）时，改实现 `PathLab.Core.IReferenceCellSetModule`：

```csharp
IReadOnlyList<Cell> ReferenceCells { get; set; }    // 界面写入；空集合表示「没有参考点」
IReadOnlyList<Cell> UsedReferenceCells { get; }     // 上次实际使用：界内、去重后的集合
```

- 载体在创建机器人后写入整个集合（`ApplyReferencePoint`）；集合为空是合法输入，
  模块必须按「无参考点」正常求解；
- 这类模块在界面上按 **toggle** 拾取：点格子加入，再点同一格移出，可连续点；
  此时 `参考点` 值标签显示 `N 个`，每格画一个 `R` 角标；
- 集合里与墙重合的坐标保留，但墙优先、永不可通行；
- 模块仍要自己校验（越界参考点 → 失败并给出 `Note`），不要静默丢弃；
  `07_min_reference_traversal` 是示例。

需要标记「本轮经过的格」而非「最远格」时，再额外实现 `PathLab.Core.IViaPointModule`：

```csharp
IReadOnlyList<Cell> RingCells { get; }  // 可选约束区域（周边格等），载体画空心方框
Cell? ViaCell { get; }                  // 上次搜索实际经过的那一格，供画 F 角标
```

载体的取舍顺序：先取 `IViaPointModule.ViaCell`，没有才用 `IReferencePointModule.FarthestPoint`；
`RingCells` 只在当前算法是 `IViaPointModule` 时绘制。

## 接受地图设施连接（传送门）

想让算法「看见」传送门等地图设施，额外实现 `PathLab.Core.ITransitionAware`：

```csharp
IPathProbe? Probe { get; set; }   // 载体在创建机器人后写入；null = 纯 4 邻接
```

```csharp
IEnumerable<Cell> NormalNeighbours(Cell cell);              // 普通 4 邻接（已滤掉墙与设施格）
IEnumerable<SpecialTransition> SpecialTransitions(Cell cell); // 设施提供的额外连接
bool IsOneWay(Cell cell);                                   // 该格是否单向通道格（缺省 false）
IEnumerable<Cell> ExitsAfter(Cell entry, Cell cell);        // 给定进入方向的单步出口（缺省 = NormalNeighbours）
```

- 两类连接**分开取**，由算法自己决定怎么计价：`00` 把传送算 1 步，`01` 算
  `TeleportCost + CostAt(ExitCell)`，未来模块可以有自己的口径；
- 设施只给连接与门费，**不排出口、不选出口、不改算法代价模型**；
- 模块必须能在 `Probe = null` 时保持原有行为（自检用 `portal A` 对照）；
- 单向通道的穿越必须直线：步行进入通道格后只能直行离开，所以带 `Probe`
  的模块应该用 `ExitsAfter(进入格, 当前格)` 取步行出口，并在通道格上把
  搜索状态按「(格子, 进入格)」分裂（路径式模块读前驱即可，无需分裂）；
  步行进入通道后不再取传送；
- 已接入：`00`、`01`、`03`、`05`、`06`、`07` 六个路径模块，以及查询模块 `02`
  （由 `03` 注入探针）。每个模块的传送计价口径见 `../PathLab/PORTALS.md` 第 1 节；
- 需要搜索克隆地图的模块（`03` / `05`）用 `Probe.For(clone)` 重绑，不要拿原图的探针去查克隆图。
- 完整设施规则见 `../PathLab/PORTALS.md` 与 `../PathLab/ONEWAY.md`。

## 执行嘲讽点必经约束

想让算法执行「必须经过所有有效嘲讽点」的约束，额外实现
`PathLab.Core.ITauntAware`：

```csharp
TauntPlan? Taunt { get; set; }   // 载体每次规划前重建；null 或空计划 = 原行为
```

- 有效集合由 `TauntPlan.Build(map, probe, points)` 计算，只包含「能纳入某条
  合法 S→G 路线」的点；不可达/封锁/访问后无法到终点的点自动排除；
- 搜索状态需扩展为 `(位置, 进入格, 已访问位掩码)`：进入某格时调用
  `Taunt.Mark(mask, cell)`，终点条件为 `cell == Goal && Taunt.Complete(mask)`；
- 状态转移与代价口径完全不变：BFS 仍按步数、Dijkstra 仍按进入代价、
  07 仍按字典序、06 仍按得分；嘲讽点只增加约束，不改目标函数；
- 特殊移动（传送门落点、跳板最终落点）落在嘲讽点上时同样置位；
  跳板连锁中间落点不是路径节点，不置位；
- 已接入：`00`、`01`、`06`、`07`；`03` / `05` 是复合目标模块，没有接入，
  载体会在状态栏提示；
- 完整规则见 `../PathLab/TAUNTS.md`。

## 单跑与对比运行

- 单选模式：顶部 `算法` 下拉框选择一个算法，载体创建一个机器人。
- `对比运行`：工具栏下方出现 `蓝 A` / `橙 B` 两栏，各自选择要对比的算法；
  载体只为这两个算法各创建一个机器人，同一张图同时规划、同时播放。
- A 固定蓝色、B 固定橙色；两栏若选了同一个算法，另一栏自动换回上一次的选择。
- 两条路径重叠时，上层路径以细芯叠在下层路径上，仍可分辨。
- 对比模式隐藏已探索点，避免两层点阵遮盖路径。

## 新增算法流程

1. 新建 `algorithms\NN_name\`，写实现与 `README.md`。
2. 同步拷贝：

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\sync-algorithms.ps1
   ```

3. 编译并自检：

   ```powershell
   dotnet build PathLab\PathLab.csproj -c Release
   PathLab\bin\Release\net10.0-windows\PathLab.exe --selftest
   ```

4. 启动载体，在下拉框选择算法；机器人会用同一套播放机制演示。
   查询模块不走这一步：它没有 `Start → Goal` 路径，由需要它的逻辑直接 `new` 出来调用。

## 当前算法

| 文件夹 | Id | 类型 | 说明 |
|---|---|---|---|
| `00_shortest_steps` | `00-shortest-steps` | Steps | BFS，步数最少，忽略格子代价 |
| `01_min_cost` | `01-min-cost` | Cost | Dijkstra，进入代价最优，步数可能更多 |
| `02_farthest_cell` | `02-farthest-from-target` | 查询 | 从指定目标点反向 BFS，返回最远可达格子与距离场 |
| `03_farthest_loiter` | `03-farthest-loiter` | 路径 | 组合 02/01/00：去程最小代价到最远格，返程最短路程回入怪口 |
| `04_radius_candidates` | `04-radius-candidates` | 查询 | 以 `floor(L * alpha)` 为基准半径，在可选整数区间内返回到入怪口可通行且可达的格子（两端相同为圆周，不同为圆环） |
| `05_obstacle_cluster_route` | `05-obstacle-cluster-route` | 路径（约束） | 参考点所在连通障碍群四周必须经过一格；在满足约束的路线中取总代价最低，失败时给出原因 |
| `06_max_perpendicular_route` | `06-max-perpendicular-route` | 路径（得分） | 标记点 `Q`：最大化 `Σ 边长 × 该边所在直线到 Q 的垂直距离`；简单路径，DFS 精确（可截断）/ 束搜索近似 |
| `07_min_reference_traversal` | `07-min-reference-traversal` | 路径（约束） | 参考点集合是软障碍：字典序 Dijkstra，先把穿越次数压到最小，再取步数最短 |

## 需要标记点的得分模块

`06_max_perpendicular_route` 也实现 `IReferencePointModule`，但它的参考点是**标记点 `Q`**：

- 提示与状态栏显示 `标记点 Q (x, y)`，不是「参考 / 最远 / 周边」；
- `FarthestPoint` 恒为 `null`：本模块没有转折点，载体画整条实线；
- 与其它模块一样由载体写入同一个参考点，对比运行中两个算法共享它。
