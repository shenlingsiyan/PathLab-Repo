---
name: rmg-v1-1-system
description: RMG V1.1 塔防随机地图生成与封堵可玩性验收工具包。当需要批量生成 16×16 塔防地图、检验"放塔能否让敌人改道"、计算地图级通过率与免费绕路占比、按目标难度区间定向出图、输出逐坐标地形/通行/代价、或对失败地图做归因时使用。触发词：生成塔防地图、地图通过率、封堵分析、最小割、免费绕路、n=1..4 组合、地图池验收、难度定向生成、diff_score、地形代价、cells.csv。
version: 1.1.0
---

# RMG V1.1 System · 塔防随机地图生成器

## 何时用

- 需要批量产塔防地图，并判断每张图"放塔有没有意义"
- 需要复跑基线、对比改动前后的通过率
- 需要按目标难度带出图（`--difficulty LO,HI`）
- 需要解释某批地图为什么不合格（独木桥 / 免费绕路 / 蹭边绕行 / 绕太远）
- 需要导出每个坐标的地形类型、通行属性与代价（`--cells`）

## 硬约束

1. 自动化批量跑必须显式给 `--seed`，否则每次结果不同、无法对照。
2. 组合上限 `--combos 60000`；16×16 的 n=4 最大约 46,000，正常不触发。
   出现 `cap_hit=1` 说明枚举被截断，结论作废（截断行 `unknown_truncated` 不计入失败）。
3. 判据阈值只在 `src/MapProfile.cs` 改；生成器不读判据。不要为了提通过率去放宽判据，
   先确认是地形问题还是判据问题。
4. 改生成器前先备份 `src/` 与 csproj 到版本库之外的目录，避免构建产物混入提交。
5. 输出目录统一放 `out/`（已在 `.gitignore` 中），不要把批次数据提交进仓库。
6. `metric_version` 只在 CSV 列/口径变化时递增；不要把它与发布版本 `1.1.0` 混用。

## 标准流程

```powershell
dotnet build -c Release

# 1. 生成 + 逐张封堵枚举（θ 引擎加 --engine theta，剖析加 --profile；
#    θ 模式起始尺寸自动钳制到 ≥11，--w 被按图拟合的 W 覆盖）
.\bin\Release\net10.0\RMG_V1_1.exe sweep --from 16 --to 16 --count 500 `
  --seed 20261025 --nmax 4 --combos 60000 --out out\<批次>\raw

# 2. 汇总（严格解析 + 数据质量报告）
python tools\summarize_sweep.py --input out\<批次>\raw --outdir out\<批次> `
  --nmax 4 --expect-maps 500 --combo-cap 60000

# 3. 通过率与首通分布（默认地图级 adaptive 口径；legacy 口径用 --metric passes_adaptive）
python tools\first_pass.py --input out\<批次>\summary_by_map.csv --outdir out\<批次> --nmax 4

# 4. 失败归因
python tools\p1_terrain.py --input out\<批次>\raw\sweep_16x16.csv --outdir out\<批次>

# 5. 地图池验收（战略通过率 + 免费绕路占比 ≤20%，含 Wilson CI）
python tools\pool_composition.py --classification out\<批次>\p1_classification.csv `
  --summary out\<批次>\summary_by_map.csv --outdir out\<批次>
```

## 地形 / 通行 / 代价输出

```powershell
# 单图写出逐坐标 CSV
.\bin\Release\net10.0\RMG_V1_1.exe --size 16 --seed 20261025 --cells out\cells.csv

# θ 批量导出时自动写同名 .cells.csv
.\bin\Release\net10.0\RMG_V1_1.exe theta --from 16 --to 16 --count 50 --export out\maps
```

字段：`x,y,terrainType,traversalType,cost`。可通行格 `cost=1`，不可跨越格
`traversalType=Impassable, cost=null`。读取者必须先检查 `traversalType`。

## 难度定向

```powershell
# 单图：目标 0.70 ≤ diff_score ≤ 0.72（区间含端点）
.\bin\Release\net10.0\RMG_V1_1.exe --size 16 --seed 20261025 --difficulty 0.70,0.72

# 批量（θ 二分封边 / v3 拒绝式选入），--difficulty 对 analyze 与 sweep 同时生效
.\bin\Release\net10.0\RMG_V1_1.exe analyze --engine theta --count 50 `
  --seed 20261025 --nmax 2 --combos 60000 --jobs 8 --difficulty 0.70,0.80 --csv out\d.csv
```

- 区间宽度建议 ≥ 0.01；0.005 起命中率明显下降
- 可达带（16×16 实测）：v3 [0.366, 0.810]、θ [0.663, 0.815]；带外必然回退缺省图
- 落空不报错（退出码 0），单图打印 `NOT reachable`，批量给出不可达计数；
  验收必须读 CSV 的 `diff_score` 列，不能只看屏幕输出
- 也可在 `Settings.DifficultyLo/Hi` 预置区间（关掉需命令行覆盖或设回 null）

## 判读

| 现象 | 含义 | 处理 |
|---|---|---|
| `mincut_natural = 1` | 独木桥，封住只能硬踩 | 确认 `MinNaturalMinCut ≥ 2` |
| `delta_zero` | 免费绕路（等长替代走廊） | 允许，但占比须 ≤ 20%（合格池分母） |
| `delta_low` | 蹭边绕行，增幅不足 8% | 地形空间不足，不是判据问题 |
| `delta_high` | 绕太远，超自适应上限 | 路径过长或门太近 |
| `overlap_only` | 新路贴着旧路 | 走廊平行，属骨架问题 |
| `half_sep_only` | 新路没跨到另一半区 | 绕行空间不足 |
| `cap_hit = 1` | 组合枚举被截断 | 提高 `--combos` 或降低 `nmax`，该行结论作废 |

参考量级（16×16 / v3 / 500 张 / seed 20261025 / n=1..4）：战略通过 81.2%（地图级 adaptive）、
免费绕路 13.1%（CI 10.2–16.6%）、严格档 63.4%；v3 ≈ 64.8 ms/图。
θ 引擎四口径全 100%、免费绕路 0%、≈ 6.5 ms/图，但全部地图同构（构造保证），
与 v3 不可直接混比。数字可用 README「复现与参考基线」的命令自行复算。

分离度诊断（`best_sep_*`）：判读以 `run`（≥3 即存在长贴边段）与 `med` 为主，
`sep_min` 对 best 样本常为 0；v3 改道约九成 run ≥ 3，θ 全部 ≤ 2。

## 故障排查

- `generation_failed` 不为 0：放宽 `MaxGenerationAttempts` 或降低 `MinNaturalMinCut`
- 汇总脚本 exit 2：读 `data_quality_report.txt`，硬错误优先于结论
- 结果与参考值差一截：先确认 seed、`--nmax/--combos` 相同，再确认没有改判据
- `--engine theta` 不认 `--size` 以外的形态参数：θ 只接受方形且 size ≥ 11
- 难度不落带：确认区间落在可达带内、宽度 ≥ 0.01，并检查 `theta_flank`（θ）或入选警告（v3）
- 坐标 CSV 缺失或与 `.map` 不一致：确认两者来自同一次生成，不要手工拼接不同批次

详细参数、字段与命令说明见 `README.md`；改动流程与验收标准见
`OPTIMIZATION_SKILL.md`；地形/通行/代价扩展规范见 `TERRAIN_COST_SKILL.md`。
