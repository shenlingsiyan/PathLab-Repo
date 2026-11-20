# 04 · 半径候选目标点（可带圆环区间）

特殊敌人的**目标点搜索**：在入怪口周围，找出空间距离落在「整数半径区间」内的格子，
并只保留可通行、且真正能走回入怪口的那些。
基准半径是 `floor(出怪口到入怪口最短步数 × 比例)`；区间两端相同即为圆周（上一版行为），
两端不同即为圆环。

本模块只做搜索，不接管机器人行为：不寻路、不停滞、不决定选哪个候选点。

| 项 | 值 |
|---|---|
| Id | `04-radius-candidates` |
| 类型 | `ICandidateTargetFinder`（查询型模块，**不是** `IPathfinder`） |
| 实现 | `RadiusCandidateFinder` |
| 默认比例 | `InitialAlpha = 0.5` |
| 复杂度 | 时间 `O(V + E)`，空间 `O(V)` |

## 口径

### 1. 最短步数 `L`

- 出怪口 = `map.Start`，入怪口 = `map.Goal`；调用方也可以传入别的入怪口。
- 由 `00_shortest_steps`（无权 BFS）计算，`L = Path.Count - 1`，即**连接边数**，不是格子数。
- 4 邻接，墙不可进入；地形代价（1..9）**不参与** `L`。
- 出怪口与入怪口重合 → `L = 0`；不可达 → 失败，`Error = Unreachable`。

### 2. 半径 `R`

```text
R = floor(L * alpha)
```

- 向下取整，不设上下限、不按地图尺寸修正、不改区间；
- `alpha` 必须是非负有限数值（负数 / `NaN` / `±∞` → `InvalidAlpha`）；
- `R = 0` 合法，只有入怪口自身可能入选。

### 3. 半径区间与圆周 / 圆环判定

以入怪口 `G` 为圆心，候选格 `P` 必须满足（`minRadius`、`maxRadius` 为整数，闭区间）：

```text
minRadius² <= (P.x - G.x)² + (P.y - G.y)² <= maxRadius²
```

- 两端相同 → 退化为精确圆周 `dx² + dy² == R²`，与上一版完全一致；
- 两端不同 → 圆环（含中间所有整数距离，如 `[3,5]` 取 `d² = 9..25`）；
- 调用方不传区间（两参为 `null`）时，两端都取基准 `R`，所以默认调用仍是圆周；
- 用整数平方（`long`）精确比较，不取平方根、不用浮点近似；
- 区间只限制**空间距离**，与移动步数、绕行、地形代价无关。

例如 `R = 5`：偏移 `(3,4)`、`(5,0)` 命中圆周，`(4,4)`、`(5,1)` 不命中；
区间 `[3,5]` 则把 `d² = 9,10,13,16,18,20,25` 等格子一并纳入。

### 4. 可达性

从入怪口做一次 BFS 构建设可达集合，再逐个候选查表，**不对每个候选重复寻路**。
本工程 4 邻接的移动边可逆，所以「能走回入怪口」等价于「从入怪口可达」。
该检查只看可达性，不要求候选到入怪口的实际步数等于区间内的半径。

## 流程

1. 校验 `map`、入怪口边界、`alpha`、区间（非负整数且下界 ≤ 上界）；
2. 复制地图，`Goal = entrance`，调 `ShortestSteps00` 得 `L`；
3. 算 `R = floor(L * alpha)`（`L * alpha` 超过 `int` 上限 → `RadiusOverflow`）；
4. 定区间：缺省的端取 `R`，得到 `MinRadius` / `MaxRadius`；
5. 从入怪口一次 BFS 得可达集合；
6. 行优先扫描全图，`min² <= dx² + dy² <= max²`、非墙、可达 → 收进 `Candidates`；
7. `Success = true`，即使 `Candidates` 为空。

行优先顺序（先 `y` 后 `x`）让同一张图的候选列表可复现。

## 输入 / 输出

```csharp
var finder = new RadiusCandidateFinder();
CandidateTargetsResult found = finder.Find(map, map.Goal, 0.5);          // 圆周，R 为半径
CandidateTargetsResult band = finder.Find(map, map.Goal, 0.5, 3, 5);     // 圆环 3..5
CandidateTargetsResult follow = finder.Find(map, map.Goal, 0.5, null, 7); // 下界缺省取 R，即 R..7

found.Success           // 是否成功执行
found.Error             // None / NullMap / EntranceOutOfBounds / Unreachable /
                        // InvalidAlpha / InvalidRadiusRange / RadiusOverflow
found.BasePathSteps     // L
found.Alpha             // 本次使用的比例
found.Radius            // R（始终按 L × alpha 计算，与区间无关）
found.MinRadius         // 实际使用的下界
found.MaxRadius         // 实际使用的上界
found.IsRing            // 两端不同即 true
found.Candidates        // 全部候选格（行优先）
found.CandidateCount    // 候选数量
found.Note              // 失败原因；成功时为 null
```

`IsOnCircle(entrance, cell, radius)` 与 `IsInBand(entrance, cell, min, max)` 是公开的静态判定，
便于单独验证圆周 / 圆环条件。单端缺省时该端取 `R`（上例 `null, 7` 即 `R..7`，`R > 7` 会返回
`InvalidRadiusRange`）。

## 边界

| 情况 | 结果 |
|---|---|
| 空地图 / 入怪口越界 | `Success = false`，`NullMap` / `EntranceOutOfBounds` |
| `alpha` 为负、`NaN`、`±∞` | `Success = false`，`InvalidAlpha` |
| 区间为负整数，或下界 > 上界 | `Success = false`，`InvalidRadiusRange`（不交换、不夹取） |
| 出怪口到入怪口不可达 | `Success = false`，`Unreachable` |
| `L = 0` 或 `alpha = 0` | `R = 0`；区间缺省时两端为 0，候选只可能是入怪口本身（仍要可通行且可达） |
| 区间两端相同 | 精确圆周，等价于上一版（`R..R`） |
| 区间两端不同 | 圆环，包含中间所有整数距离 |
| 圆周 / 圆环部分在地图外 | 界外坐标直接忽略 |
| 候选格是墙 / 被隔离 | 不进候选 |
| 候选数量为 0 | **成功**，`CandidateCount = 0`，不是失败 |
| 多个候选 | 全部返回，行优先 |
| 地形代价改为 9 | `L` 与候选不变（只有通行状态改变才允许结果变化） |
| 坐标平方 | `long` 计算，避免溢出 |

## 测试（`--selftest`）

| 用例 | 构造 | 断言 |
|---|---|---|
| 基础半径 | 13×3 走廊，`L = 12`，`alpha = 0.5` | `BasePathSteps = 12`，`Radius = 6` |
| 边数与格子数 | 7 格走廊 | `L = 6`，`R = 3` |
| 圆周判定 | `R = 5` | `(3,4)/(5,0)` 命中，`(4,4)/(5,1)` 不命中 |
| 障碍过滤 | 圆周格涂墙 | 该格不在候选 |
| 不可达过滤 | 圆周格被墙围死 | 该格不在候选 |
| 多候选 | 11×11 开阔图，`L = 10`，`R = 5` | 12 个候选全部返回 |
| 空候选集 | `R` 大于地图跨度 | `Success = true`，`CandidateCount = 0` |
| 地形代价 | 最短路径格改为 9 | `L` 不变、候选不变 |
| 半径为零 | `Start = Goal`；`alpha = 0`；区间 `[0,0]` | `R = 0`，候选为入怪口 |
| 圆环区间 | 同一张图取 `[3,5]` | 圆环候选多于圆周、距离 1 与 3 的格子分别在外/在内 |
| 区间退化 | `[5,5]` | 与默认圆周调用逐项相同 |
| 覆盖全图 | `[0,20]` | 121 个可通行格全部入选 |
| 单端缺省 | `3..null`、`null..2` | 前者等价 `[3,5]`，后者 `InvalidRadiusRange` |
| 区间非法 | `[4,2]`、`[-1,3]`、`[2,-4]` | `InvalidRadiusRange`，不自动交换 |
| 参数 / 空图 / 越界 | — | 对应 `Error`，`Success = false` |
| oracle | 独立实现（BFS `L` + 反向可达 + 整数平方区间） | `L` / `R` / `MinRadius` / `MaxRadius` / 候选列表逐项一致 |

## 约束

- 不改地图生成、不改普通敌人寻路、不做最长简单路径；
- 不用曼哈顿距离替代欧几里得距离，不把圆环改成半径区间之外的近似判定；
- 不对区间做交换、夹取或自动修正；两端相同即圆周、不同即圆环，都是合法配置；
- 不代表敌人实际移动步数，也不进入 `IPathfinder` 下拉框与对比运行；
- 停滞计时、候选挑选与后续移动属于敌方状态机，不在本模块内。
