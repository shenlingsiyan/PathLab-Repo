---
name: lexicographic-soft-obstacle
description: 07_min_reference_traversal 受约束路径模块的制作要点——参考点集合是软障碍、字典序 Dijkstra (穿越数, 步数) 的成立条件与实现、软障碍永不设成永久障碍、嘲讽点约束下允许路径重复格、状态扩展 (位置, 进入格, 掩码)。当用户要实现/修改「最小化穿越/必经集合」类字典序约束算法时使用。
---

# 07_min_reference_traversal · 字典序软障碍模块

## 口径

参考点集合是**软障碍**：字典序目标 `(ReferenceCount, PathLength)`——
能 0 次穿越绝不穿越，必须穿越时数量最少，同数量取步数最短。
起终点不计入穿越；普通墙永远不可通行。

## 字典序 Dijkstra 要点

- 成立条件只有两条：**每条边分量非负**、**字典序对加法保序**；满足即可用
  `PriorityQueue<Cell, (穿越, 步数, Y, X)>`；
- 同一格会多次入队：出队后比对 `best[]` **丢弃过期记录**，旧代价不能继续扩展；
- 平局用 `(Y, X)` 固定顺序 → 同图同路径（确定性）；
- **硬禁止**：加权折中（`0.001×穿越 + 步数`）或大惩罚系数冒充字典序——
  优先级不是绝对优先级，总会在某些图上翻车。

## 嘲讽点接入

- 状态扩展为 `(Cell Here, Cell From, int Mask)`；进入格子置位，终点条件
  `Here == Goal && Complete(mask)`；
- **`CheckPath` / `ValidatePathfindingResult` 加 `allowRepeats` 参数**：收集死胡同里的
  嘲讽点需要合法回返，原「路径不得重复格」的审计在有嘲讽约束时放开——
  审计规则要跟随口径变化，否则合法解会被自己人拒绝；
- 失败时仍填 `TauntRequiredCount` 与 `MissedTauntCells`，Note 给「无法同时经过全部
  N 个嘲讽点」——调试信息完整，不静默丢点。

## 自检要点

- 独立 oracle 重算 `(穿越数, 步数)` 并比对；A–M 测试矩阵（见
  `skills/make-pathfinding-algorithm/SKILL.md` 第 5 节）全集适用；
- 软障碍与墙重合 → 墙优先；参考点集合为空 → 退化为纯步数最优且与 `00` 对照。
