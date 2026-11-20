# facilities — 地图设施（特殊移动机制）

设施是「不改算法、不改地图代价、只发布额外连接」的特殊格子机制。每种设施 =
`PathLab/Core/` 里的一个实现文件 + `PathLab/` 下的一份规则文档 + 本目录的一份制作 skill。

| 设施 | 实现 | 规则文档 | 制作 skill |
|---|---|---|---|
| 混乱传送门 | [PortalFacility.cs](../PathLab/Core/PortalFacility.cs) | [PORTALS.md](../PathLab/PORTALS.md) | [portal/](portal/) |
| 单向通道 | [OneWayFacility.cs](../PathLab/Core/OneWayFacility.cs) | [ONEWAY.md](../PathLab/ONEWAY.md) | [one-way/](one-way/) |
| 指向跳板 | [DirectionalLauncherFacility.cs](../PathLab/Core/DirectionalLauncherFacility.cs) + [LauncherPathPlan.cs](../PathLab/Core/LauncherPathPlan.cs) | [LAUNCHERS.md](../PathLab/LAUNCHERS.md) | [launcher/](launcher/) |
| 单次嘲讽点 | [TauntPointFacility.cs](../PathLab/Core/TauntPointFacility.cs) + [TauntPlan.cs](../PathLab/Core/TauntPlan.cs) | [TAUNTS.md](../PathLab/TAUNTS.md) | [taunt/](taunt/) |

## 设施的统一设计（先读这个）

1. **数据与呈现分离**：设施是纯数据（格子、参数、启用状态），不涂改地图代价，
   画布按设施类型绘制图标（门格 P、单向箭头、跳板箭头+距离、嘲讽点红方块）；
2. **探针包装**：设施通过 `ApplyTo(IPathProbe inner, GridMap map)` 包装成探针链，
   叠加顺序 `_launchers → _oneWay → _portals`，每层转发内层再追加自己的连接；
3. **计价归算法**：设施发布连接（`SpecialTransition` 带可选代价字段），但**不决定**
   代价——BFS 算 1 步、Dijkstra 算门费+落点地形费、得分模块权重 0、07 按参考点计穿越；
4. **动效归回放**：特殊移动在 `SearchResult` 里留事件（触发 ID、落点序列），
   机器人按事件播放，独立审计可从坐标路径重放验证；
5. **每次规划即时重算**：设施无缓存，地图/参数刚被编辑也在下一次寻路生效；
   16×16 规模下实测足够。

新增设施照着 `skills/pathlab-architecture/SKILL.md` 第 7 节的六步清单走。
