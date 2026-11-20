# 07 · 最少参考点穿越最短路径

参考点是**软障碍**：地图上允许走，但能不踩就不踩。本模块求一条从 `S` 到 `E` 的
**简单路径**（4 邻接、不进墙、不出界、不重复格子），按**严格双层（字典序）**最优：

```text
第一优先级：ReferenceCount 最小   （路径进入的参考点个数）
第二优先级：PathLength    最小   （移动步数）
```

两层之间**不允许互相补偿**：`0 次穿越 / 30 步` 永远优于 `1 次穿越 / 5 步`。
只有当所有合法路线都必须穿过参考点时，才允许穿越，且穿越数量必须最少。

| 项 | 值 |
|---|---|
| Id | `07-min-reference-traversal` |
| 类型 | 路径模块 `IPathfinder` + `IReferenceCellSetModule` |
| 实现 | `MinimumReferenceTraversalPathfinder` |
| 目标 | `Objective = Steps`（步数是第一层之后的裁决项），`Optimal = true` |
| 约束 | `Constrained = true`：只承诺**在参考点约束下的**字典序最优，代价可与无约束最短路不同 |
| 复杂度 | `O((V + E) log V)`，16×16 图上是 256 个格子，毫秒级 |
| 结果字段 | `ReferenceCount`（`SearchResult` 为本模块新增）、`IsOptimal`、`TerminationReason` |

## 口径

### 1. 参考点集合

```csharp
module.ReferenceCells = new[] { new Cell(2, 2), new Cell(5, 7) };  // 可以为空
```

- 集合可以为空 → 退化为「步数最短路」，与 `00_shortest_steps` 同长；
- 越界坐标 → 失败（`Note = 参考点超出地图边界：(x, y)`，`TerminationReason = 输入无效`），
  不静默丢弃；
- 重复坐标去重，`UsedReferenceCells` 是**去重后（按 `y`、`x`）**的实际集合；
- 落在墙上的参考点保留在集合里，但墙优先：该格永远不可通行，也就永远不会被计入穿越。

### 2. 参考点 vs 普通障碍

| 格子 | 能否进入 | 是否计入穿越 |
|---|---|---|
| 普通障碍（`cost == 0`） | 永不 | — |
| 参考点（非起终点） | 可以 | 是（每次进入 +1） |
| 参考点在墙上 | 永不（墙优先） | 否 |
| 起点 / 终点本身 | 固定闸门 | **否** |

起终点是固定位置，不是路线主动选择的中间格，因此即使它们在参考点集合里也不计入。
路径一旦重复经过同一个参考点，那就是重复访问同一个格子——本模块的路径本身不允许重复格子，
所以同一参考点最多计 1 次。

### 3. 逐边代价

```text
进入 V 的代价：
    ReferenceCost = 1  当 V 是参考点 且 V ≠ Start 且 V ≠ Goal，否则 0
    MoveCost      = 1  （每次移动固定一步）

NewCost = (当前 ReferenceCount + ReferenceCost, 当前 PathLength + MoveCost)
比较：先比 ReferenceCount，相等再比 PathLength
```

不用加权折中（如 `0.001 × 穿越 + 步数`）也不建议「把参考点设成永久障碍」：
前者可能在某些地图上被长度补偿掉，后者一旦参考点封死全部通路就直接失败。

## 流程

1. 校验：地图为空、`S/E` 越界或为墙、参考点越界 → 失败并给出 `Note`；
2. `S == E`：唯一简单路径是单格 `[S]`，`PathLength = 0`、`ReferenceCount = 0`，直接判最优；
3. 二元代价 Dijkstra（见下）；
4. 出队取到 `E` 且该条记录仍是最新代价时结束搜索；
5. 按前驱反向重建路径，独立校验后回填结果。

```text
best[S] = (0, 0);  queue.Enqueue(S, (0, 0, S.Y, S.X))

while queue 非空:
    (u, key) = queue.Dequeue()                     // key = (crossings, moves, y, x)
    过期检查：best[u] 与 key 的 (crossings, moves) 不一致 → 丢弃这条记录
    explored.Add(u)
    u == E → 结束
    for v in Neighbours(u):                        // 右、左、下、上，固定的 4 邻接顺序
        v 是墙 → 跳过
        crossings = key.crossings + (v 是参考点 且 v 不是 S/E ? 1 : 0)
        moves     = key.moves + 1
        若 best[v] 的字典序 ≤ (crossings, moves) → 跳过
        best[v] = (crossings, moves); previous[v] = u
        queue.Enqueue(v, (crossings, moves, v.Y, v.X))
```

优先队列的比较键是 `(crossings, moves, y, x)`：前两项是真正的字典序代价，
后两项只是让同一代价的候选按行优先固定顺序出队，保证同一张图**每次得到同一条路径**。

### 正确性

- 每条边的两个分量都非负（0/1 与恒 1），且字典序加法保持顺序：
  `A ≺ B ⇒ A + e ≺ B + e`，所以 Dijkstra 的「首次弹出即定稿」成立；
- 弹出 `E` 的那一刻，堆中没有更小的字典序代价，因此穿越数是全局最小，
  且该穿越数下步数最短；
- 每次移动 `PathLength` 严格 +1、穿越数不下降，最优路径不可能含环，因此自然满足
  「不重复格子」；`previous` 链不会回绕；
- 出队时比对 `best[u]` 丢弃过期记录：一个格子可能因更好的代价多次入队，
  旧记录不能继续用旧的（更大的）代价扩展邻格；
- 不允许把参考点当永久障碍，否则「参考点封死所有无穿越通路」时无法给出备用路线；
  也不使用固定大惩罚系数，避免出现惩罚之和被步数补偿的边界情况。

## 结果字段

`PathfindingResult` 的各个字段在载体里落在 `SearchResult` 上，一一对应：

| 规格字段 | 实现 | 说明 |
|---|---|---|
| `Success` | `Found` | 是否找到合法路线 |
| `Path` | `Path` | `S → … → E`，不含重复格子 |
| `ReferenceCount` | `ReferenceCount` | 由最终路径独立重算，不是搜索累加器 |
| `PathLength` | `Path.Count - 1` | 移动步数 |
| `IsOptimal` | `IsOptimal` | 正常跑完即 `true`；失败一律 `false` |
| `ExpandedNodes` | `Explored.Count` | 实际出队并扩展的格子数 |
| `SearchTime` | `ElapsedMs` | 本模块自己的计时（载体还会用整体计时覆盖） |
| `TerminationReason` | `TerminationReason` | 见下表 |

| `TerminationReason` | `IsOptimal` | 含义 |
|---|---|---|
| `找到最少穿越的最短路径` | `true` | 字典序最优解（含 `S == E`） |
| `不存在合法路径` | `false` | 墙把 `S` 与 `E` 完全隔开（`Note` 同文） |
| `输入无效` | `false` | 空图 / 起终点越界或为墙 / 参考点越界（`Note` 给出细节） |
| `路径重建校验失败` | `false` | 重建出的路径不合法（实现 bug 的信号，绝不返回该路径） |

`Cost` 仍按项目统一口径填写该路径的**实际进入代价之和**（与 00/01 可比），
但它不是本模块的优化目标。

## 独立验证

```csharp
public static string? MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(
    SearchResult? result, GridMap? map, IReadOnlyList<Cell>? referencePoints)
```

只遍历最终的 `Path`，**不读取搜索过程里的任何累加器**，重新检查：

1. 是否从 `S` 出发、是否到达 `E`；
2. 相邻节点必须 4 方向相邻（曼哈顿距离 = 1）；
3. 是否越界、是否穿过普通障碍；
4. 是否有重复坐标；
5. 重新统计穿越数量（跳过 `S`/`E`、参考点去重），与 `result.ReferenceCount` 比对。

全部一致返回 `null`，否则返回第一条问题描述。搜索内部也调用同一套 `CheckPath`，
一旦失败就返回 `路径重建校验失败`，而不是把非法路径交给界面。

```csharp
int count = MinimumReferenceTraversalPathfinder.CountReferences(path, map, references);
```

`CountReferences` 是同一个口径的公开版本，供界面/报告复用。

## 测试（`--selftest`）

| 用例 | 构造 | 断言 |
|---|---|---|
| A 无参考点 | 5×5 开放图，集合为空 | 8 步、0 穿越；与 `00_shortest_steps` 同长；= oracle `(0, 8)` |
| B 参考点不阻断 | 同上，参考 `(2,2)`、`(1,3)` | 0 穿越、仍 8 步（绕开不额外收费）；= oracle |
| C 参考点在最短路线上 | 5×3，`S=(0,1)` `E=(4,1)`，参考 `(2,1)` | 0 穿越、6 步，明确比 4 步直达更长；= oracle `(0, 6)` |
| D 只能穿门 | 5×5，`y=2` 整行墙只留 `(2,2)` 一个门，该门是参考点 | 1 穿越、路径含 `(2,2)`；= oracle |
| E 多点同时阻断 | 5×3，`(0,0)`/`(0,2)` 是墙，参考 `(1,1)`、`(3,1)` | 只穿 1 个；= oracle（不是「遇到第一个就停」） |
| F 同穿越数取最短 | 4×3，`(2,0)` 是墙，参考 `(2,1)`、`(2,2)` | 1 穿越且取 3 步的 `(2,1)` 门，不走 5 步的 `(2,2)` 门 |
| G 短路径穿多个 vs 长路径穿一个 | 同 E 图 | 取 6 步 / 1 穿越，放弃 4 步 / 2 穿越 |
| H 参考点 = 起点 | 5×5，参考 `(0,0)` | 0 穿越、8 步 |
| I 参考点 = 终点 | 5×5，参考 `(4,4)` | 0 穿越、8 步 |
| J 重复参考点 | D 图，同一坐标写三次 | 计数 1；`UsedReferenceCells.Count == 1`；验证器通过 |
| K 参考点在墙上 | 5×5，墙 `(1,1)` 同时是参考点，另加 `(2,2)` | 0 穿越，路径不含 `(1,1)` |
| L 墙彻底阻断 | 5×5，整列 `x=2` 是墙，参考 `(2,2)` | `!Found`，`Note`/原因均为「不存在合法路径」，`!IsOptimal` |
| M 起点 = 终点 | 3×3，`S=E=(1,1)`，参考 `(0,0)` | 1 格、0 穿越、`IsOptimal` |
| 输入校验 | 越界参考点；先涂墙再设终点；`Search(null)` | 分别报「参考点…」/「终点不可通行」/「地图为空」，均为 `输入无效` |
| 验证器反例 | 把 1 次穿越的结果伪造成 `ReferenceCount = 0` | `ValidatePathfindingResult` 必须返回非 `null` |
| N 复现 | 同实例连跑两次 | 同一条路径、同一穿越数 |

oracle 口径：自检里有一个**不剪枝的独立递归枚举**，遍历全部简单路径，只保留
「4 邻接、不进墙、到 `E`」的路线，按 `(穿越数, 步数)` 取字典序最小。
它与被测模块共享的只有 `GridMap.Neighbours`，代价规则在自检里独立重写，
避免「用被测代码验证被测代码」。

## 界面

- 选中本算法后出现 `参考点` 一行，提示为
  `点格子加入参考点(再点移出) · 参考点为软障碍：先少穿越、再最短`；
- `选参考点` → `点击格子…` 后可连续点格子：第一次加入，再点同一格移出（集合显示为 `N 个`），
  每个参考点画一个 `R` 角标；
- 状态栏：
  `最少参考点穿越最短路径 · 12 步 · 穿越 0 · 找到最少穿越的最短路径 · 参考点 3 个 · 穿越 0 · 扩展 78 · 0.4 ms`；
- 对比运行压缩成 `12步/0穿`，不挤占另一家算法的位置；
- 换图会清空参考点集合；把参考点涂成墙时，墙规则立即生效（该格不再可走）。

## 约束

- 只读地图：不改 `GridMap`、不改随机地图生成器、不改普通敌人的寻路路径；
- 新模块独立实现，`00`/`01` 等既有模块行为完全不变；
- 复用 `GridMap.Neighbours` / `CostAt` / `IsWall`，不复制一套邻接规则；
- 优化目标严格两层，**不加入**转弯惩罚、地形权重、路径偏好等第三目标；
- 失败时返回 `Found = false` 与原因，不伪造路径，也不把墙当作可穿越参考点；
- 模块无跨实例共享状态：`best` / `previous` / `explored` 都在单次 `Search` 内，
  对比运行时两个实例互不影响。

## 视觉与实现保持极简

一个类、一个优先队列、两张字典（`best` / `previous`），外加三个公开静态函数
（`CountReferences`、`ValidatePathfindingResult`、内部 `CheckPath`）。
界面只复用已有的「参考点一行 + 角标 + 状态栏」三条通道，不新增控件行、不加动画、
不加缓存层；`R` 角标与既有单参考点角标同形，不引入新的视觉语言。

## 传送门（特殊连接）

本模块实现 `ITransitionAware`，并给 `ValidatePathfindingResult` 增加一个可选探针参数：

```csharp
module.Probe = probe;
MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(result, map, references, probe);
```

- 传送算**一步**；穿越数按**落点**判：落点是参考点（且不是起终点）就 +1，
  门格本身是墙，永远不会成为参考点；
- 字典序比较不变 `(穿越次数, 步数)`，所以「传送能少走很多步但落点要穿越」时，
  仍然先看穿越数（自检 `portal 07`：宁可换一个落点也不踩参考点）；
- 路径重建校验把「探针确实提供的传送」认作合法步，其它不相邻跳变仍报错；
- `Probe = null` 时与 `07` 原有行为完全一致。
