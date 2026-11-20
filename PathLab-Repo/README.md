# PathLab — 网格寻路算法可视化实验台

一个 .NET 10 / WinForms 的网格寻路可视化载体：同一张地图、同一套机器人播放机制，
同时演示多种寻路算法与地图设施（传送门 / 单向通道 / 指向跳板 / 单次嘲讽点）的相互作用。
本仓库的另一个身份是**教材**：目录结构与文档专为 agent 与新人学习「如何把玩法
翻译成可证明最优的路径算法」而组织。

## 快速开始

```powershell
# 1. 同步算法源到载体（新增/修改算法后都要执行）
powershell -ExecutionPolicy Bypass -File tools\sync-algorithms.ps1

# 2. 编译
dotnet build PathLab\PathLab.csproj -c Release

# 3. 无窗口自检（全部通过才算可用）
PathLab\bin\Release\net10.0-windows\PathLab.exe --selftest

# 4. 启动可视化
PathLab\bin\Release\net10.0-windows\PathLab.exe
```

界面内可放/拆障碍、涂改空地代价、放置地图设施、随时生成新随机地图（优先调用
RMG，失败回退内置），并用同一套机器人播放机制演示不同算法。勾选 `对比运行`
后可让两个算法在同一张图上同时规划、同时播放。

## 目录结构

| 目录 | 作用 |
|---|---|
| `PathLab/` | 可视化载体源码：`Core/`（地图、契约、设施、机器人）、`Ui/`（画布、主窗体）、`SelfTest.cs`（无窗口自检） |
| `algorithms/` | 寻路算法**唯一源目录**，每个算法一个文件夹：实现 + `README.md`（口径）+ `SKILL.md`（制作要点） |
| `facilities/` | 四种地图设施的说明与制作 skill（设施实现在 `PathLab/Core/*Facility.cs`） |
| `skills/` | 跨算法沉淀的方法论 skill（架构哲学、算法制作流程） |
| `tools/` | `sync-algorithms.ps1`：把算法源拷贝进载体后编译 |
| `RMG V1_1 SYSTEM/` | 随机地图生成器（独立 C# 程序，PathLab 优先调用它生成 16×16 地图，失败回退内置生成器） |

`PathLab/Algorithms/` 是**生成目录**，不要手改；`.gitignore` 已排除，每次改动算法源后重跑 sync 脚本。

## Agent 学习路线

按顺序读，每一步都有可运行的代码对应：

1. **架构哲学** → [skills/pathlab-architecture/SKILL.md](skills/pathlab-architecture/SKILL.md)
   载体分层、探针机制、能力接口按需生长、状态分裂、诚实性原则。**先读这个**。
2. **算法制作流程** → [skills/make-pathfinding-algorithm/SKILL.md](skills/make-pathfinding-algorithm/SKILL.md)
   八轮实战沉淀的口径 → 算法 → 契约 → oracle → 测试矩阵 → 界面 → 文档流程。
3. **基础算法** → `algorithms/00_shortest_steps`（BFS 步数）与 `algorithms/01_min_cost`
   （Dijkstra 代价）：最简单的 `IPathfinder` 实现，看懂契约的最小样本。
4. **特殊算法** → `algorithms/02_*` 到 `algorithms/07_*`：查询模块、组合模块、
   受约束模块、得分模块，每个文件夹有 README（口径）与 SKILL.md（制作要点）。
5. **地图设施** → [facilities/README.md](facilities/README.md)：传送门、单向通道、
   指向跳板、单次嘲讽点。每个设施演示「设施只发布连接、不改算法目标函数」这一核心设计。
6. **自检** → `PathLab/SelfTest.cs`：所有正确性承诺都用可重跑的断言兑现。

## 算法一览

| 文件夹 | Id | 类型 | 一句话 |
|---|---|---|---|
| `00_shortest_steps` | `00-shortest-steps` | 路径 | BFS，步数最少 |
| `01_min_cost` | `01-min-cost` | 路径 | Dijkstra，进入代价最优 |
| `02_farthest_cell` | `02-farthest-from-target` | 查询 | 反向 BFS 距离场，最远可达格子 |
| `03_farthest_loiter` | `03-farthest-loiter` | 路径 | 去最小代价到最远格，最短路返程 |
| `04_radius_candidates` | `04-radius-candidates` | 查询 | 圆周/圆环上的可达候选点 |
| `05_obstacle_cluster_route` | `05-obstacle-cluster-route` | 路径（约束） | 必须绕行参考点所在障碍群 |
| `06_max_perpendicular_route` | `06-max-perpendicular-route` | 路径（得分） | 简单路径最大化垂直距离得分 |
| `07_min_reference_traversal` | `07-min-reference-traversal` | 路径（约束） | 参考点是软障碍：字典序 Dijkstra |

## 地图设施一览

| 设施 | 规则文档 | 实现源码 |
|---|---|---|
| 混乱传送门 | [PathLab/PORTALS.md](PathLab/PORTALS.md) | `PathLab/Core/PortalFacility.cs` |
| 单向通道 | [PathLab/ONEWAY.md](PathLab/ONEWAY.md) | `PathLab/Core/OneWayFacility.cs` |
| 指向跳板 | [PathLab/LAUNCHERS.md](PathLab/LAUNCHERS.md) | `PathLab/Core/DirectionalLauncherFacility.cs` + `LauncherPathPlan.cs` |
| 单次嘲讽点 | [PathLab/TAUNTS.md](PathLab/TAUNTS.md) | `PathLab/Core/TauntPointFacility.cs` + `TauntPlan.cs` |

## 契约速查

```csharp
// 路径模块
public interface IPathfinder
{
    string Id { get; } string Name { get; } string Summary { get; }
    SearchObjective Objective { get; } bool Optimal { get; } bool Constrained => false;
    SearchResult Search(GridMap map);
}

// 能力接口（按需实现，载体自动检测）
ITransitionAware   { IPathProbe? Probe }        // 看见传送门/单向/跳板等设施连接
ITauntAware        { TauntPlan? Taunt }         // 执行嘲讽点必经约束
IReferencePointModule { Cell? ReferencePoint }  // 单参考点（界面写入）
IReferenceCellSetModule { IReadOnlyList<Cell> ReferenceCells } // 参考点集合（toggle 拾取）
IViaPointModule    { RingCells / ViaCell }      // 约束区与实际经过格

// 查询模块
IFarthestPointFinder { FarthestPointResult Find(GridMap map, Cell target) }
ICandidateTargetFinder { ... Find(GridMap map, Cell entrance, double alpha, int? min, int? max) }
```

设施探针组合顺序（`MainForm.ApplyFacilities`）：

```
_launchers.ApplyTo(_oneWay.ApplyTo(_portals.CreateProbe(_map)), _map)
```

设施只发布连接，**计价口径留在算法里**（BFS 传送算 1 步、Dijkstra 算门费+落点地形费、
得分模块传送权重 0、07 落点按参考点计穿越），算法目标函数从不因设施而改变。

## 详细文档

- 载体操作与架构：[PathLab/README.md](PathLab/README.md)
- 算法目录约定与契约细节：[algorithms/README.md](algorithms/README.md)
- 各设施规则：见上表
