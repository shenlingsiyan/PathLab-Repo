# 指向跳板（Directional Launcher）

固定方向 + 可修改**正整数转移距离**的弹射设施。`Target = Position + 方向 × 距离`，
中途格子不检查（能飞过墙）；落点是启用跳板时**强制连锁**、不可中断；
最终落点越界 / 不可通行 / 循环则整条移动不可用；禁用的跳板按普通格处理。
跳板格仍可普通穿越，主动使用与普通穿越**并列**（站上跳板格或其四邻格都可触发）。
完整规则见 [../PathLab/LAUNCHERS.md](../PathLab/LAUNCHERS.md)。

- 数据：`DirectionalLauncher { Id, Position, Direction, TransferDistance, Enabled }`；
- 连接：`SpecialTransitions(格)` 对跳板格本身与四邻格各发布候选，
  `TransitionType.Launcher`，事件带触发 ID 序列与每次落点序列；
- 错误模型：`LauncherChainError { Disabled, OutOfBounds, Blocked, Cycle }`，
  循环用独立访问集合（`HashSet<int>`），**不用次数上限替代**；
- 回放：`LauncherPathPlan.Build` 从坐标路径重放非 4 邻接步，事件入
  `SearchResult.LauncherJumps` 供机器人播放与独立审计。
