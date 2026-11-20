---
name: pathlab-architecture
description: PathLab 网格寻路可视化载体的系统架构思路与兼容性设计精华：源目录/编译副本分离、探针统一设施接入（设施只发布连接不改算法目标函数）、能力接口按需生长、搜索状态分裂、诚实性原则与自检闸门。当用户要理解/修改/扩展 PathLab 架构、新增地图设施、让算法兼容新机制、排查「设施与算法相互作用」类问题时使用。
---

# PathLab 架构思路 · 系统设计与算法兼容性精华

PathLab 的核心命题：**一个载体，N 个算法，M 种设施，任意组合都不需要改任何一方的代码。**
它的解法不是框架化，而是五条纪律。每条纪律都能在仓库里指到具体文件。

## 0 一图流

```
algorithms/NN_name/*.cs          ← 唯一算法源（含 README.md 口径文档）
        │  tools/sync-algorithms.ps1（只拷贝，绝不手改副本）
        ▼
PathLab/Algorithms/NN_name/      ← 编译副本（生成物，.gitignore 排除）
        │
Core/  GridMap      地图：代价格（0=墙，1..9=进入费），Start/Goal 不可涂墙
       IPathfinder  算法契约 + SearchResult（含 Score/LauncherJumps/嘲讽统计等字段）
       IPathProbe   设施探针：NormalNeighbours / SpecialTransitions / ExitsAfter
       *Facility    四种设施：Portal / OneWay / DirectionalLauncher / TauntPoint
       Robot        持有独立算法实例，按 Path 逐格播放
Ui/    GridCanvas   画格 + 设施绘制 + 设施编辑事件（PaintMode 互斥）
       MainForm     控件、探针组合、每次规划前重建计划、状态栏
SelfTest.cs          130+ 断言：正确性承诺的唯一兑现处
```

## 1 纪律一：源目录与编译副本分离

- 算法**只**在 `algorithms/` 维护；`PathLab/Algorithms/` 是 sync 脚本的输出。
- 改了算法源必须重跑 `tools\sync-algorithms.ps1`，否则跑的是旧代码——这是本项目
  出现过的最隐蔽的坑（编译通过 ≠ 跑的是新逻辑）。
- 好处：算法模块保持「只依赖 `PathLab.Core`、不引用 WinForms」的干净边界，
  可以脱离 UI 被 `--selftest` 直接调用。

## 2 纪律二：设施走探针，算法只看见「两类连接」

```
IPathProbe {
    IEnumerable<Cell> NormalNeighbours(Cell cell);                  // 普通 4 邻接（已滤墙与设施格）
    IEnumerable<SpecialTransition> SpecialTransitions(Cell cell);   // 设施发布的额外连接
    bool IsOneWay(Cell cell);                                       // 该格是否单向通道格
    IEnumerable<Cell> ExitsAfter(Cell entry, Cell cell);            // 按进入方向收紧出口（缺省=普通邻接）
}
```

- **设施只发布连接，不排序出口、不选出口、不改地图代价、不改算法目标函数。**
  计价口径永远留在算法里：`00` 传送算 1 步、`01` 算门费+落点地形费、`06` 传送权重 0、
  `07` 落点按参考点计穿越。
- 适配新设施时先问一句：**「这个算法的目标函数怎么给一次特殊移动定价？」**
  答不出来就说明该模块不该接入，而不是硬塞。
- 探针是**包装器链**，设施叠加 = 包装：

```
MainForm.ApplyFacilities:
  _launchers.ApplyTo(_oneWay.ApplyTo(_portals.CreateProbe(_map)), _map)
```

  每层包装转发内层查询再追加自己的连接。新增设施 = 写一个 `IFacility.ApplyTo(inner, map)`，
  不动任何已有层。
- 无设施时探针等价于纯 4 邻接（`OneWayFacility.Plain(map)`），算法在 `Probe = null`
  时必须保持原有行为——这是自检的对照基准。
- 门格「不可停留」的实现：**把设施格设为墙并记住原价**，于是对所有算法一次成立，
  不需要任何算法做特殊判断，也不需要改地图数据结构。

## 3 纪律三：能力接口按需生长，载体自动检测

主干只实现 `IPathfinder`。能力接口只在真需要时新增，载体用 `is` 检测推送数据：

| 能力接口 | 触发的 UI 行为 |
|---|---|
| `ITransitionAware { IPathProbe? Probe }` | 创建机器人后写入组合探针 |
| `ITauntAware { TauntPlan? Taunt }` | 每次规划前写入重建的嘲讽计划 |
| `IReferencePointModule { Cell? ReferencePoint }` | 显示「参考点」行与选点按钮 |
| `IReferenceCellSetModule { IReadOnlyList<Cell> ReferenceCells }` | toggle 拾取（点一次加入、再点移出） |
| `IViaPointModule { RingCells / ViaCell }` | 画约束区方框与经过格角标 |

规则：
- **用两个独立 `if` 推送能力接口**，不要 `if / else if`——一个类实现两个接口时会漏推；
- 用户输入由载体 `Apply...` 推给模块，**模块自己校验**（越界 → 失败 + `Note`，
  绝不静默替换）；空集合、`null` 都是合法输入，等价于「无此约束」；
- 每个机器人持有**独立算法实例**（`AlgorithmHost.Create`），对比运行互不干扰；
  模块可以保留与自身实例相关的状态，但不得依赖跨实例全局状态。

## 4 纪律四：搜索状态分裂——约束兼容的通用武器

当约束依赖「怎么进的这一格」或「路上吃过什么」，就把状态从 `Cell` 扩展成元组：

| 机制 | 状态 | 说明 |
|---|---|---|
| 单向通道 | `(Cell Here, Cell From)` | 通道格按进入方向分裂；步行进入后只能直行离开；传送落点无几何进入（From = 自身） |
| 单次嘲讽点 | `(Cell Here, Cell From, int Mask)` | 进入格子时 `Mark(mask, cell)` 置位；终点条件 `Here == Goal && Complete(mask)` |
| 指向跳板 | 不扩状态 | 跳板是 SpecialTransition，落点即新位置；连锁在设施层解析 |

关键兼容规则（嘲讽点 × 特殊移动）：
- 特殊移动**落点**落在嘲讽点上 → 置位掩码；
- 跳板**连锁中间落点**不是路径节点 → 不置位；
- 嘲讽点有效集合 `T'` 由 `TauntPlan.Build(map, probe, points)` 在 **`(位置, 进入格)` 状态图**
  上做正反双向可达判定——不能只查「起点可达」，还要查「访问后能到终点」；
  无解时返回「无合法路径 + 调试信息」，绝不静默丢弃有效点；
- 计划**每次规划前重建**（`MainForm.PlanAll`），地图/设施刚被编辑也绝不复用旧计划。

通用教训：**约束加进状态，代价函数不动**。BFS 还是按步数、Dijkstra 还是按进入代价、
07 还是按字典序——设施只增加约束，不改目标函数。

## 5 纪律五：诚实性 + 自检闸门

- `IsOptimal = true` 只在**证明过**之后写；预算耗尽返回现有最好合法解 + `未证明最优`；
- `TerminationReason` 是固定短语表，README、界面、测试引用同一字面量；
- oracle 必须**与被测代码不同源**（只共享 `Neighbours/CostAt/IsWall`），权重公式、
  计数口径在自检里重写一遍；
- 特殊设施的事件回放也走独立审计：跳板路径用 `LauncherPathPlan.Build` 从坐标路径
  重放非 4 邻接步并匹配设施连接，无法解释则整条路径无效；
- `--selftest` 是唯一验收口径：`dotnet build` 0 警告 + selftest 全绿 + GUI 冒烟。

## 6 算法 × 机制兼容矩阵（当前事实）

| 模块 | 类型 | 探针(传送/单向/跳板) | 参考点 | 嘲讽点 | 备注 |
|---|---|---|---|---|---|
| 00 BFS | 路径 Steps | ✅ | — | ✅ | 最小样本 |
| 01 Dijkstra | 路径 Cost | ✅ | — | ✅ | 门费+落点地形费 |
| 02 反向 BFS | 查询 | ✅（由 03 注入） | 调用方给定 | — | 无路径，不进下拉框 |
| 03 周旋 | 路径组合 | ✅ | ✅ 单点 | ❌ 提示不执行 | 两段腿，`Probe.For(clone)` 重绑克隆图 |
| 04 半径候选 | 查询 | — | 入怪口 | — | 允许成功但 0 候选 |
| 05 障碍群周边 | 路径约束 | ✅ | ✅ 必须是墙 | ❌ 提示不执行 | `Constrained=true` 跳过通用最优对照 |
| 06 垂直得分 | 路径得分 | ✅ | ✅ 标记点 Q | ✅ | 简单路径 DFS/Beam，独立评分函数 |
| 07 字典序 | 路径约束 | ✅ | ✅ 集合 | ✅ | 软障碍，`(穿越数, 步数)` 字典序 |

❌ 不是漏洞：03/05 是**复合目标模块**（两段路线/约束汇合），叠加必经点约束会改变
其路线定义。正确做法是让载体在状态栏**明确提示**「该算法不执行嘲讽点约束」，
绝不静默违反约束，也不为它们硬造一个混合目标函数。

## 7 新设施接入清单（已被四种设施验证的路径）

1. **数据类**：`Core/XxxFacility.cs`——数据 record + 增删改 + 解析/查询方法；
   解析失败给结构化错误（如 `LauncherChainError { OutOfBounds, Blocked, Cycle }`），
   循环检测用**独立访问集合**，不用次数上限；
2. **探针包装**：`ApplyTo(IPathProbe inner, GridMap map)`——转发内层、追加自己的连接；
   需要地图信息（墙判定等）时通过参数传 map，探针不暴露 map；
3. **UI**：`GridCanvas.PaintMode` 加互斥工具 + 绘制方法 + 编辑事件；`MainForm` 加控件、
   实例、事件处理器，挂进 `ApplyFacilities` 包装链；
4. **路径回放**：特殊移动要在结果里留事件（触发 ID、每次落点），供机器人播放与独立审计；
   状态栏把设施事件并入已有统计行（如 `跳板 N 次`），能复用就不加新控件；
5. **自检**：规则断言 + 至少一组「寻路端到端走设施」断言 + 失败路径断言；
6. **文档**：`PathLab/XXX.md` 规则文档 + `facilities/<name>/SKILL.md` + 三份 README 各补一行。

## 8 极简主义（贯穿所有层）

- 一个设施一个类文件，一个能力接口一个文件；没有基类、插件框架、DI 容器；
- 设施代价默认 0，几何距离与寻路成本分离，各算法自行计价；
- UI 新增控件行之前先问「能不能复用参考点行 / 状态栏」；
- 文档不写空话：每个数字、每条规则都能指到代码或自检断言。

## 9 踩坑实录（架构层）

- sync 后忘重编译/忘重跑 sync → 跑的是旧算法；任何「改动没生效」先查这个；
- 探针包装层需要 map 时，不要给 probe 加 map 属性，改 `ApplyTo(inner, map)` 签名；
- `SpecialTransition` 加字段用**默认参数值**，老调用点（传送门）零改动；
- 无缓存 ≠ 性能差：四种设施在 16×16 图上全都是每次查询即时重算，先测量再优化；
- 状态扩展后**所有**终点判定、所有 goal 过滤处都要带上新分量（06 有两个 goal 判定点），
  漏一处就会出现「有时违反约束」；
- 测试「可达但无法到终点」的用例，必须同时封死回返通道，否则有效集合判定会误判。
