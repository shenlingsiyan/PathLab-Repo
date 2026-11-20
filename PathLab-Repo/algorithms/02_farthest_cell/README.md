# 02 · 最远可达格子（反向 BFS）

特殊敌人行为的第一步：**给定一个参考点（入怪口），找出可达格子中距离它最远的格子。**

本模块只做这一件事。前往最远点、停滞、返回入怪口由现有的最小代价 / 最小路程算法与敌人状态机完成，
不在本模块内。

| 项 | 值 |
|---|---|
| Id | `02-farthest-from-target` |
| 类型 | `IFarthestPointFinder`（查询型模块，**不是** `IPathfinder`） |
| 实现 | `FarthestFromTargetFinder` |
| 复杂度 | 时间 `O(V + E)`，空间 `O(V)` |

## 距离的定义

在**可通行格子构成的图**中，从某格到指定目标点的**最少移动步数**：

- 每次移动计 1 步，四邻接（上、下、左、右），与载体 `GridMap.Neighbours` 完全一致；
- 墙（代价 0）不可进入，也不计入距离；
- 地形代价（1..9）**不参与**本距离，`普通 / 泥泞 / 高价` 格子都只算一步；
- 不是直线距离、不是曼哈顿距离、不是最长简单路径，也不是允许重复经过时的最长走法。

例：某格到入怪口最短 12 步，即使存在 20 步的绕行路线，它的距离仍是 12。

## 算法

1. 校验目标点：必须在地图内，否则返回失败。目标点可以是空地，也可以是障碍。
2. 建 `distance[x, y]` 数组，全部置 `-1`（不可达）：
   - 目标点是空地 → 目标点置 `0` 并入队；
   - 目标点是障碍 → 它的每个可通行邻格置 `1` 并入队（走出障碍算第一步）；
     四周没有可通行格时返回失败。
3. 队列非空时出队一格 `current`：对每个邻接格，跳过墙与已访问格，
   置 `distance = distance[current] + 1` 并入队。**反向**遍历保证每个可达格拿到的是到目标点的最短路步数。
4. 行优先（先 `y`、后 `x`）扫描全图，统计最大距离与所有并列格子；`candidates[0]` 即最终最远点。
5. 返回 `{ Found, Cell, Distance, Candidates, Distances }`。

障碍目标点（例如开在墙上的门洞）不会出现在候选里——距离场只覆盖可通行格，
`DistanceAt(障碍目标点)` 仍是 `-1`；最远点、候选与并列规则不受影响。

并列规则是确定性的：同一输入永远得到同一个最远点；`Candidates` 保留全部并列格，
需要随机策略时在调用方从 `Candidates` 里自己抽，不改变 BFS 结果。

## 输入 / 输出

```csharp
var finder = new FarthestFromTargetFinder();
FarthestPointResult far = finder.Find(map, entrance);   // entrance 是 Cell

far.Found          // 是否成功
far.Cell           // 最远可达格子
far.Distance       // 该格到入口的步数
far.Candidates     // 全部并列最远格（行优先顺序）
far.Distances      // int[x, y]，-1 = 墙或不可达；其余为到入口的步数
far.DistanceAt(cell)  // 越界安全的读取
far.Note           // 失败原因
```

`Distances` 可直接给可视化层画距离场，不必二次 BFS。

## 敌人行为的衔接

最远点计算与移动路径规划是分离的，本模块只负责第一阶段：

```csharp
// 1) 选目标：入怪口就是地图的 Start
FarthestPointResult far = finder.Find(map, map.Start);
if (!far.Found) return;                     // 参考点越界或四周全墙，交给敌人状态机处理

// 2) 前往最远点：最小代价（Dijkstra），从敌人当前位置出发
GridMap view = map.Clone();
view.Start = enemy.Cell;                    // 敌人当前格
view.Goal = far.Cell;                       // 最远格
SearchResult toTarget = new MinCostPathfinder().Search(view);

// 3) 停滞：由项目的计时系统处理，本模块与可视化载体都不参与

// 4) 返回入怪口：最小步数（BFS）
view.Start = enemy.Cell;                    // 停滞结束时所在格
view.Goal = map.Start;                      // 入怪口
SearchResult back = new ShortestStepsPathfinder().Search(view);
```

`GridMap` 的 `Clone()` 会保留尺寸与地形，只改 `Start` / `Goal`，因此两个现有算法无需改动即可复用。
调用前请确认 `enemy.Cell` 不是墙、`enemy.Cell != view.Goal`（同格时 `Path` 只有一格）。

上面四阶段的完整接线已经落在 `../03_farthest_loiter/`（`距参考点最远周旋算法`）：
它调用本模块选点，再交给 `01_min_cost` 与 `00_shortest_steps` 走完两段，拼成一条可视化的
`Start → 最远点 → Goal` 路线。本模块自身仍然只做「选点」。

## 为什么不是 `IPathfinder`

`IPathfinder` 的契约要求返回一条 `Start → Goal` 的路径，由载体机器人逐格播放，并参与 `--selftest`
的路径校验与对比运行。本模块的输出是「一个格子 + 距离场」，没有 S→E 路径，因此实现了
`IFarthestPointFinder`，不出现在界面的算法下拉框里，也不会被 `对比运行` 选中。
这是刻意的：不把最远点计算塞进路径算法契约，也不为了展示而伪造路径。

## 边界情况

| 情况 | 行为 |
|---|---|
| 地图为空（`null`） | `Found = false`，`Note = "地图为空"` |
| 目标点越界 | `Found = false`，`Note = "目标点超出地图边界"` |
| 目标点是墙 | 以其可通行邻格为距离 `1` 的种子；四周全墙时 `Found = false`，`Note = "参考点四周没有可通行格子"` |
| 存在不可达区域（墙隔开的孤岛） | 只统计可达格；不可达格保持 `-1`，绝不作为候选 |
| 多个并列最远点 | 全部进入 `Candidates`，取行优先第一个 |
| 只有一个可通行格子 | 返回该格本身，`Distance = 0` |
| 环路 / 死路 / 孤立区 | BFS 天然处理；距离取最短路，不受绕行影响 |

## 验证

`PathLab.exe --selftest` 覆盖：开阔图最远角、并列候选顺序、墙体隔离的不可达区、
单格地图、越界目标、空地/障碍两种目标点、四周全墙的障碍目标、空地图、重复调用结果一致、
代价 9 格子不影响步数，并把 `Distances` 与一份独立 BFS 逐格比对。

## 传送门（特殊连接）

本模块实现 `ITransitionAware`。`03` 会把探针注入进来：

```csharp
finder.Probe = probe;   // null = 纯 4 邻接
```

- 反向 BFS 每层额外接受门连接，传送算 1 步；门连接是全对多且双向的，反向搜索
  可以直接把「出口格 → 入口格」当普通边读，不需要另写一套反向规则；
- 只有门才能进的口袋因此也会参与「最远」比较：距离里包含那一步传送；
- `Probe = null` 时 `Distances` 与接入前完全一致（自检 `portal 02` 对照）。
