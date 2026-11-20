# 03 · 距参考点最远周旋算法

特殊敌人的完整行为，以「一条 Start → 最远点 → Goal 的路线」呈现在可视化载体里。

| 项 | 值 |
|---|---|
| Id | `03-farthest-loiter` |
| 类型 | `IPathfinder` + `IReferencePointModule` |
| 实现 | `FarthestLoiterPathfinder` |
| Objective | `Steps`（声明 `Optimal = false`：两段目标不同，整体不对单一指标最优） |
| 依赖 | `02_farthest_cell`、`01_min_cost`、`00_shortest_steps` 三个模块的公开类 |

## 四阶段与代码的对应

| 阶段 | 任务描述 | 实现 |
|---|---|---|
| 1 | 找距参考点最远的可达格 | `FarthestFromTargetFinder.Find(map, reference)`（反向 BFS） |
| 2 | 出怪口 → 最远格，最小累计代价 | `MinCostPathfinder`，图副本 `Start = map.Start`、`Goal = 最远格` |
| 3 | 抵达后停滞 | 载体不做停滞；真实游戏由自身计时系统处理 |
| 4 | 最远格 → 入怪口，最少步数 | `ShortestStepsPathfinder`，图副本 `Start = 最远格`、`Goal = map.Goal` |

两段路径在最远格拼接（去掉重复的衔接格），`Path` 因此从 `map.Start` 到 `map.Goal`，
满足 `IPathfinder` 契约，机器人沿用同一套播放、单步、重置机制。

载体上 **实线 = 去程（最小代价）**，**虚线 = 返程（最短路程）**，转折点画 `F`。

## 出入口与参考点

- `map.Start` = **出怪口**（敌人从这里出发）；
- `map.Goal` = **入怪口**（敌人最后回到这里，也是路径终点）；
- 参考点由用户指定，**同一时刻只有一个**；`ReferencePoint = null` 时默认用 `map.Goal`（入怪口）；
- 参考点可以是**空地或障碍**：障碍参考点（例如开在墙上的门洞）由 02 从其可通行邻格起步，
  距离从 1 开始算，不会静默替换成别的格子；
- 只有越界参考点、或四周全为墙的参考点才返回失败；
- 参考点可以不是出入口：`R` 标记即当前参考点，`F` 是它对应的最远格。

## 输出

```csharp
var module = new FarthestLoiterPathfinder { ReferencePoint = someCell };
SearchResult route = module.Search(map);   // Path: Start -> 最远格 -> Goal
module.UsedReference;                      // 本条结果实际使用的参考点
module.FarthestPoint;                      // 去返程的转折格
```

- `Cost` 是整条路线（两段合并）的**实际进入代价之和**，不是最优代价；
- `Explored` 是两段搜索的访问序列拼接，用于单算法模式下的探索点显示；
- `Note` 说明失败原因：`地图为空` / `参考点超出地图边界` / `参考点四周没有可通行格子` /
  `出怪口无法抵达最远点` / `最远点无法返回入怪口`。

## 边界情况

| 情况 | 行为 |
|---|---|
| 参考点与出怪口不连通 | 第一阶段成功但第二阶段失败 → 整体 `Found = false`，仍保留 `R`/`F` 标记 |
| 最远格恰好是出怪口 | 去程退化为单格，机器人直接走返程 |
| 最远格恰好是入怪口 | 返程退化为单格，路线在入怪口结束 |
| 多个并列最远点 | 由 02 的行优先规则决定，结果确定 |
| 墙参考点（邻格可通行） | 照常成功：02 以邻格为距离 1 的种子，`R`/`F` 标记与两段路线不变 |
| 参考点越界 / 四周全墙 | 失败并给出原因 |
| 只有一个可通行格 | 最远格即该格；若它同时是终点，路线只有一格 |

## 为什么声明 `Optimal = false`

去程按代价最优、返程按步数最优，但拼接后的整条路线既不保证总代价最小，
也不保证总步数最小（它必须经过最远格）。`--selftest` 因此不做最优性对照，
只校验：路径从 S 到 E、4 邻接、不穿墙、报告代价与路径逐格求和一致、访问序列非空。

## 复用而不是复制

本模块直接调用另外三个模块的公开类，不复制 BFS / Dijkstra 代码。
同步脚本会把 `algorithms/` 下所有文件夹一起拷进载体，所以它们始终在同一个程序集里；
如果删除 `00` / `01` / `02` 中任意一个，本模块将无法编译。

## 传送门（特殊连接）

本模块实现 `ITransitionAware`，探针会同时送到三处：

```csharp
module.Probe = probe;                             // 02 选最远点
new MinCostPathfinder { Probe = probe.For(map) }  // 去程
new ShortestStepsPathfinder { Probe = probe.For(map) } // 返程
```

- 最远点由 02 用门计算，所以「只有门能进」的口袋也会成为候选；
- 两段腿都跑在克隆图上，用 `Probe.For(clone)` 重绑，而不是拿原图的探针；
- 报 `Cost` 时每次传送补一次门费（与 `01` 同一口径）；
- 返程先尝试避开去程格子（保证复合路线是简单路径），避不开（口袋只能原路出来）才回退到
  不避让的第二趟，此时复合路线可能复用格子——本模块不声称最优，README 与 `PORTALS.md` 都写明；
- `Probe = null` 时行为与本文件前面的描述一致（自检 `portal A` / 既有用例对照）。
