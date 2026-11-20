# RMG V1.1 System — 塔防随机地图生成器

16×16 塔防地图生成 + 封堵可行性分析的一体化工具包。目标不是"生成连通合法的地图"，
而是生成**放塔后敌人会改道、改道看得见、绕得值**的地图。

- 发布版本：**1.1.0**
- 引擎：`v3` 拒绝采样（默认）/ `theta` 骨架构造
- 判据：重叠 ≤ 0.35、半分离 ≥ 0.55、增幅 ∈ [8%, 35% + 5%·(n−1)]
- 验收：战略通过率 + 免费绕路占比 ≤ 20%
- 难度：三维参考分 `diff_score`，支持按目标区间定向生成
- 坐标输出：每个坐标携带地形类型、通行属性、可通行代价

运行环境：.NET 10 SDK；Python 3.9+（仅统计工具需要，纯标准库）。

> 本目录由 `RMG system`（V5 代码库）整合而来。生成算法、判据阈值、随机数顺序与
> `metric_version` 均未改变；变更仅为发布命名、目录整理和文档更新。详见文末「版本」。

## AI 快速理解

先读本 README，再读 `SKILL.md`（操作流程）和 `OPTIMIZATION_SKILL.md`（改动准则）。
需要扩展地形/代价时读 `TERRAIN_COST_SKILL.md`。

建议源码阅读顺序：

```text
src/MapTerrain.cs      地形类型、通行属性、代价目录
src/MapProfile.cs      判据阈值与策略质量常量
src/MapGenerator.cs    主流程、Settings、CLI、最短路、v3 拒绝采样、难度选入
src/MapSkeleton.cs     θ 骨架生成、装饰、封边比例二分、导出
src/MapAnalyzer.cs     封堵枚举、最小割、分离度、难度评分、CSV
src/MapLoader.cs       .map 回读与路径复算校验
```

必须保持的不变量：

1. 全项目只有一个寻路实现：`MapGenerator.FindPath`，生成、分析、运行时共用。
2. `MapData` 的 `Obstacles / Spawn / Exit` 是唯一地图状态；坐标输出经
   `GetTerrainCell` 投影，不另建格子网格。
3. `v3` 是拒绝采样，`theta` 是构造式骨架；二者只在生成阶段分派，之后共用打印与分析层。
4. 判据阈值只在 `MapProfile.cs`；生成器不读判据。
5. 同 seed 结果确定：Dijkstra tie-break 固定为 `(cost, y, x)`。
6. `metric_version` 只在 CSV 列/口径变化时递增；发布版本号与度量版本号是两件事。

## 目录

```text
RMG V1_1 SYSTEM/
  RMG_V1_1.csproj          工程文件（net10.0，输出 RMG_V1_1.exe，版本 1.1.0）
  README.md                本文：总览、AI 阅读顺序、命令、字段、基线
  SKILL.md                 使用型 skill：何时用、硬约束、标准流程、判读表
  OPTIMIZATION_SKILL.md    优化型 skill：审计顺序、验收标准、禁止事项
  TERRAIN_COST_SKILL.md    地形代价扩展 skill：通行属性与逐坐标输出规范
  src/
    MapGenerator.cs        生成器主流程、Settings、CLI 分派、拒绝采样、难度选入
    MapAnalyzer.cs         封堵枚举、最小割、难度评分、CSV 输出、判据实现
    MapProfile.cs          判据阈值与全局常量
    MapSkeleton.cs         θ 骨架引擎（含封边比例二分）
    MapLoader.cs           --load / --load-dir 回读校验
    MapTerrain.cs          地形/通行/代价的集中定义与坐标投影
  tools/
    summarize_sweep.py     原始 CSV → 汇总（严格解析 + 数据质量报告）
    first_pass.py          每张图首次通过所需封锁数 n、累积通过率
    p1_terrain.py          失败类型归因（delta_zero / delta_low / …）
    pool_composition.py    地图池构成与免费绕路占比（含 Wilson CI）
    explain_failures.py    单通道与多判据失败的证据表
    verify_p0.py           共享列回归校验（--raw 单文件 / --baseline 对比）
    check_p8_columns.py    CSV 表尾追加列与版本自检
  out/                     运行期输出（自己创建，未纳入版本库）
```

## 快速开始

```powershell
# 构建
dotnet build -c Release

# 生成一张地图（交互模式，按任意键退出；恒打印难度块）
.\bin\Release\net10.0\RMG_V1_1.exe --size 16 --seed 20261025

# 生成一张指定难度带的地图（0.70 ≤ diff_score ≤ 0.72）
.\bin\Release\net10.0\RMG_V1_1.exe --size 16 --seed 20261025 --difficulty 0.70,0.72

# 批量生成 + 封堵分析（最常用）
.\bin\Release\net10.0\RMG_V1_1.exe sweep `
  --from 16 --to 16 --count 500 --seed 20261025 --nmax 4 --combos 60000 `
  --out out\run1\raw

# 汇总
python tools\summarize_sweep.py --input out\run1\raw --outdir out\run1 `
  --nmax 4 --expect-maps 500 --combo-cap 60000

# 通过率与首通分布（默认即地图级 adaptive 口径）
python tools\first_pass.py --input out\run1\summary_by_map.csv --outdir out\run1 --nmax 4

# 失败归因
python tools\p1_terrain.py --input out\run1\raw\sweep_16x16.csv --outdir out\run1

# 地图池构成（2:8 验收）
python tools\pool_composition.py --classification out\run1\p1_classification.csv `
  --summary out\run1\summary_by_map.csv --outdir out\run1
```

## 命令参考

| 命令 | 用途 |
|---|---|
| `sweep --from W --to W --count N --seed S --nmax N --combos C --out DIR` | 批量生成并逐张做 n=1..N 封堵枚举，写出 `sweep_WxH.csv` |
| `sweep ... --engine v3\|theta` | 切换生成引擎；θ 模式起始尺寸自动钳制到 ≥ 11 |
| `sweep ... --jobs N` | 按图并行批量分析（缺省 1 = 顺序；任何 N 下 CSV 与顺序模式逐字节一致） |
| `sweep ... --profile` | 附加分阶段剖析，写出 `profile_report.txt`（不改变任何生成/分析结果） |
| `sweep ... --difficulty LO,HI` | 难度目标透传到每个尺寸的批次，语义同 `analyze --difficulty` |
| `analyze --count N --nmax N --seed S --combos C --csv PATH [--engine ...]` | 同 sweep，但输出到指定 CSV（默认 `analysis/baseline_v1_1_soft.csv`） |
| `analyze ... --difficulty LO,HI` | 批量难度定向：θ 每图二分封边比例；v3 沿 seed 序列选入 |
| `theta --from W --to W --count N [--tier 1\|2] [--export DIR]` | θ 批量生成与导出（`尺寸\tier_seed.map` + 同名 `tier_seed.cells.csv` + `manifest.json`） |
| `--size N --seed N --tier N` | 主流程单张生成的覆盖参数（恒打印 `=== Difficulty ===`） |
| `--size N --seed N --cells PATH` | 主流程单张生成后写出逐坐标 CSV（地形 / 通行属性 / 代价） |
| `--size N --seed N --difficulty LO,HI` | 单图难度定向（生成阶段直接生效） |
| `--load FILE` / `--load-dir DIR` | 回读 `.map` 并复核路径（`--load-dir` 按 manifest 校验） |
| `sep-selftest` / `u-selftest` | 分离度 / U-pattern 指标的人工地图单元测试 |

## 关键参数（`src/MapGenerator.cs` 的 `Settings`）

| 参数 | 默认 | 作用 |
|---|---|---|
| `MapWidth / MapHeight` | 16 | 地图尺寸（θ 要求方形且 ≥ 11） |
| `MinPathLengthBetweenGates` | 18 | 门间最短路径下限：太短会让每个封堵的相对增幅虚高 |
| `MinNaturalMinCut` | 2 | 门间顶点不相交走廊下限：1 就是"独木桥"，封了只能硬踩、无法改道 |
| `ObstacleRatio` | 0.25 | 障碍预算上限（v3） |
| `MinTurns / MaxTurns` | 4 / 14 | 路径转数区间（v3） |
| `MinPathRatio / MaxPathRatio` | 1.10 / 2.10 | 路径长度 / 空图曼哈顿距离（v3） |
| `MinUPaths / MaxUPaths` | 1 / 2 | U 型路径计数（v3） |
| `MaxGenerationAttempts` | 10000 | 拒绝采样上限（v3） |
| `UseTheta` | false | 引擎开关：`false` = v3，`true` = θ |
| `DifficultyLo / DifficultyHi` | null / null | 难度预设：设区间后**生成阶段**直接选入，`analyze`/`sweep` 同源沿用；null = 关闭 |

改判据阈值只改 `src/MapProfile.cs`；生成器不读取判据，二者解耦。

## 判据与验收口径

单张地图、某个封锁数 n 判定为通过，需同时满足：

1. `switched` —— 敌人完全避开封锁格（不是付罚金硬穿）
2. `best_overlap ≤ 0.35` —— 新路不贴着旧路走
3. `best_half_sep ≥ 0.55` —— 新路跨到地图另一半
4. `8% ≤ best_delta ≤ 35% + 5%·(n−1)` —— 绕路要多走，但不能多到没人愿意封

地图级验收：

- **战略通过率**：只要某个 n 存在合格组合即算通过（地图级 adaptive 口径，见 `map_pass_*` 列）
- **免费绕路占比 ≤ 20%**：存在等长替代走廊（`delta_zero`）视为允许的偶然；
  分母 = 战略通过 + 免费绕路

> 口径提醒：`passes_other_half` / `passes_adaptive` 只看最高评分样本，会漏判
> "存在合格组合但最佳样本不合格"的地图（n 越大漏得越多）。正式统计一律用 `map_pass_*`，
> legacy 列仅用于展示与对照。

## 难度：评分与定向生成

`ComputeDifficulty` 是**只读诊断**（不参与判据、不影响生成结果），三维等权：

| 维度 | 计算 | 方向 |
|---|---|---|
| d1 路程 | `(MaxPathRatio − path_ratio) / (MaxPathRatio − MinPathRatio)`，截断到 [0,1] | 路越短越难 |
| d2 开阔 | 主路每格 ±1 八邻中「空格 / (空格+墙)」 | 周边越空旷越难守 |
| d3 U 型 | 数量分（0 个 → 1.0 / 1 个 → 0.5 / ≥2 个 → 0）与规模分（`1 − U总格数/30`）各半 | U 越少越小越难 |
| `diff_score` | 三维均等加权 1/3 | 越高 = 越难防守 |

**定向生成**（`--difficulty LO,HI`，或 `Settings.DifficultyLo/Hi` 预设）实现方式因引擎而异：

| 引擎 | 机制 | 实测可达带（16×16） |
|---|---|---|
| θ | 每图二分封边比例（8 次探测） | [0.663, 0.815] |
| v3 | 拒绝式选入：单图换派生 seed（`seed + k·7919`，预算 40 次）；批量沿 seed 序列扫描，预算 = count×10，凑满即停 | [0.366, 0.810] |

实用区间宽度：≥ 0.01 可靠（实测 10/10 命中），0.005 起开始落空。
落空不报错：返回缺省图并标注 `NOT reachable` / 批末汇总不可达数，退出码仍为 0；
参数非法（如 `0.9,0.5`）才以退出码 1 终止。θ 的实际封边比例写入 `theta_flank` 列留痕。

## 输出字段

`sweep_*.csv` 每行是一张图的某个封锁数：

| 字段 | 含义 |
|---|---|
| `combos` / `combos_expected` / `cap_hit` | 实际枚举、理论组合数、是否触碰上限 |
| `switched` / `tolled` / `equal_cost` | 完全改道 / 仍踩封锁格 / 改道后代价不变 |
| `passes_other_half` / `passes_adaptive` | 最优样本口径：严格 / 自适应判据是否通过（legacy 展示列） |
| `passing_combinations` / `passing_adaptive_combinations` | 通过判据的组合数 |
| `map_pass_other_half` / `map_pass_adaptive` | 地图级口径：只要存在 ≥1 个合格组合即通过 |
| `map_pass_status` | `pass` / `fail_complete`（完整枚举无合格组合）/ `unknown_truncated`（截断未知，不计入失败） |
| `best_sep_min` / `best_sep_adj2` / `best_sep_med` / `best_sep_run` | 路线分离度诊断：best 改道相对旧路的 BFS 步距最小值 / ≤2 步占比 / 中位数 / 最长连续贴边段。仅诊断，不参与判据 |
| `diff_d1` / `diff_d2_open` / `diff_d3_ucount` / `diff_d3_usize` / `diff_score` | 难度诊断 5 列（仅参考口径，不参与判据） |
| `theta_flank` | θ 实际封边比例（`--difficulty` 调制后取值，缺省 1.000；v3 行留空） |
| `best_overlap` / `best_half_sep` / `best_delta` / `best_shift` | 最优组合的重叠、半分离、增幅、位移 |
| `mincut_natural` / `choke_count` | 顶点最小割（独立走廊数）/ 瓶颈数 |
| `half_sep_tier` | premium（≥ 0.70）/ usable（≥ 0.55）/ fail |
| `metric_version` | 判据与列版本，当前 `p9-v7` |

## 坐标输出（地形 / 通行 / 代价）

逐坐标数据与 `.map` 同源，格式为 CSV（`--cells PATH`，或 θ `--export` 时自动写
`<尺寸>/t<tier>_<seed>.cells.csv`）：

```csv
x,y,terrainType,traversalType,cost
0,0,Empty,Traversable,1
1,0,NaturalObstacle,Impassable,null
2,0,Spawn,Traversable,1
```

- `terrainType`：沿用生成器现有地形来源（`Empty` / `NaturalObstacle` / `Spawn` / `Exit`）；
  主路 `+` 只是路线叠加，不改变地形类型。
- `traversalType`：`Traversable` / `Impassable`；不可跨越只由该字段表达，不用巨大代价代替。
- `cost`：可通行格沿用 P0 寻路内核的自然格规则（每步 1）；不可跨越格写 `null`，
  读取者必须先检查 `traversalType`。
- 所有坐标都来自同一份 `MapData`，地形与障碍生成结果天然一致；生成算法与随机数调用顺序未改。

## 复现与参考基线

同 seed 序列结果完全确定（Dijkstra tie-break 固定为 `(cost, y, x)`；并行只影响吞吐，不影响输出）。

```powershell
# 参考基线参数：500 张 / seed 20261025 / n = 1..4 / 组合上限 60000
.\bin\Release\net10.0\RMG_V1_1.exe sweep --from 16 --to 16 --count 500 `
  --seed 20261025 --nmax 4 --combos 60000 --out out\check\raw
```

参考值（16×16，v3，500 张，seed 20261025，n = 1..4）：

| 口径 | n=1 | n=2 | n=3 | n=4 | 累计任一 n |
|---|---|---|---|---|---|
| 地图级 adaptive（正式） | 43.2% | 71.2% | 77.6% | 81.2% | **81.2%** |
| 最优样本 adaptive（legacy） | 42.4% | 60.8% | 66.2% | 72.6% | 75.8% |
| 地图级 strict | 37.4% | 55.2% | 61.4% | 63.4% | 63.4% |
| 最优样本 strict（legacy） | 36.6% | 44.0% | 43.4% | 40.6% | 54.4% |

免费绕路占比 13.1%（Wilson CI 10.2–16.6%）。这些数字可由上面的命令自行复算验证。

θ 引擎（同 seed 同 500 张）：四口径 n=1..4 全部 100% 通过、免费绕路 0%、生成零失败。
但 θ 的通过是构造保证（双廊 + 强制单格缺口），全部地图同构（单一地形类），
与 v3 的多类异质地形不可混同——通过率不能直接横向比较。

成本量级（24 逻辑核机）：v3 ≈ 64.8 ms/图，θ ≈ 6.5 ms/图；`--jobs 8` 下 v3 500 张
32.0 s → 5.2 s（6.16×），θ 3.6 s → 1.1 s（3.31×）。超出物理核数无额外收益。

## 已知边界

- 单图组合枚举上限 `--combos 60000`；16×16、n=4 时最大约 46,000，正常不触发
- `cap_hit` 出现即为分析不完整，应增大上限或降低 `nmax`；截断行 `unknown_truncated`
  不计入失败，此时地图级通过率是下界
- θ 引擎不接受 `--size` 以外的 v3 形态参数；只接受方形且 size ≥ 11
- `--jobs N` 只是吞吐旋钮：任何 N 下 CSV 与顺序路径逐字节一致
- `--difficulty` 是**软约束**：可达带外或区间过窄时会回退缺省图并给出提示，
  需要硬保证时请自行校验 CSV 的 `diff_score` 列
- θ 的封边比例 < 1 会放松"封边迫使改道"的设计语义，低难度档位的判据指标需随取随查
  （`theta_flank` 列留痕）
- 难度分是**参考口径**（d1/d2/d3 等权），不是玩家实测难度；分数落带 ≠ 手感必然符合
- 主流程单图恒打印难度块：该块只影响屏幕输出，不改变地图生成与任何 CSV
- 免费绕路占比超 20% 时优先检查是否放宽过判据，而不是收紧生成器
- `best_sep_*` 只描述 best 样本：`sep_min` 常为 0（best 允许少量共享格），分离判读看
  `run`（≥3 即存在长贴边段）与 `med`；v3 改道约九成存在 run ≥ 3，θ 全部 ≤ 2

## 度量版本

判据阈值始终只在 `src/MapProfile.cs`；表尾诊断列只增不改，`metric_version` 随之递增：

| 版本 | 内容 |
|---|---|
| `p1-v3` | 初始：纯 best-sample 口径 |
| `p1-v4` | + 地图级三态列 `map_pass_*`（正式统计口径） |
| `p3-v5` | + 表尾 4 个分离度诊断列 `best_sep_*`（判据零变化） |
| `p8-v6` | + 表尾 5 个难度诊断列 `diff_*`（仅参考口径） |
| `p9-v7` | + 表尾 1 列 `theta_flank`（θ 实际封边比例） |

跨版本数据不要混合统计：先按 `metric_version` 分桶，再做对比。

## 版本

| 项 | 值 |
|---|---|
| 发布版本 | `1.1.0` |
| 工程 / 可执行文件 | `RMG_V1_1.csproj` / `RMG_V1_1.exe` |
| 根命名空间 | `RMG_V1_1` |
| 度量版本 | `p9-v7`（口径未变） |
| 整合来源 | `RMG system`（V5 代码库） |

1.1.0 的变更：

1. 新建独立目录与工程命名，发布版本升到 1.1.0；源码主文件改为 `src/MapGenerator.cs`。
2. 纳入地形代价与逐坐标输出（`MapTerrain.cs`、`--cells`、`.cells.csv`）。
3. 文档整合为 README + 三份 skill：使用、优化、地形代价扩展。
4. 生成算法、判据阈值、随机数顺序、难度评分、`metric_version` 均未改变。

> 历史注释中的 `P0..P11` 是内部实现阶段编号，`p1-v3..p9-v7` 是 CSV 度量版本号；
> 二者都不是发布版本，不要与 `1.1.0` 混用。
