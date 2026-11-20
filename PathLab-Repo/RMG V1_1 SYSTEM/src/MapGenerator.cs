using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RMG_V1_1
{
    internal static partial class Program
    {
        // ============================================================
        // USER SETTINGS
        // Edit this section to tune map generation.
        // ============================================================
        private static class Settings
        {
            // Map size. U-shaped paths need more room, so V1.1 defaults to 16 x 16.
            public static int MapWidth = 16;
            public static int MapHeight = 16;

            // Set UseRandomSeed = false to reproduce the same map.
            public static bool UseRandomSeed = true;
            public static int RandomSeed = 20261022;

            // Both gates are selected from opposite boundary regions (see
            // ChooseRandomBoundaryCell for the diagonal bias).
            // Minimum shortest-path distance between gates, measured in grid moves.
            // S1: raised from 1 to 18. Short routes inflate the relative detour of
            // every blocker, which is what pushes maps past the delta ceiling.
            public static int MinPathLengthBetweenGates = 18;

            // S2: minimum number of vertex-disjoint corridors between the gates.
            // A value of 1 means a single mandatory passage: any block covering it
            // cannot be routed around, so the enemy always pays the toll instead of
            // switching. Requiring 2 removes those maps at generation time.
            // 0 disables the constraint.
            public static int MinNaturalMinCut = 2;

            // 以下四项（障碍比例与簇参数、形态约束、U 型约束、尝试上限）只在 UseTheta = false 时生效。
            // θ 引擎不参与这些判据：密度用它自己的软目标，形态由拓扑保证。
            // Natural obstacle settings.
            // This is a target maximum budget; obstacle count will not exceed it.
            public static double ObstacleRatio = 0.25;
            public static int MinClusters = 2;
            public static int MaxClusters = 5;
            public static int MinClusterSize = 3;
            public static int MaxClusterSize = 8;
            public static double ClusterGrowthChance = 0.70;
            public static int MinScatteredObstacles = 2;
            public static int MaxScatteredObstacles = 7;

            // General path-shape constraints.
            public static int MinTurns = 4;
            public static int MaxTurns = 14;
            public static int MinSegmentLength = 1;
            public static int MaxSegmentLength = 6;

            // Actual shortest-path length / empty-map Manhattan distance.
            public static double MinPathRatio = 1.10;
            public static double MaxPathRatio = 2.10;

            // U-pattern constraints.
            // One U-pattern is a non-overlapping group of 5 straight segments
            // (4 consecutive turns) whose first and last directions are opposite.
            public static int MinUPaths = 1;
            public static int MaxUPaths = 2;

            // Number of random candidate maps to try before giving up.
            public static int MaxGenerationAttempts = 10000;

            // 引擎选择：false = v3 拒绝采样（默认），true = θ 骨架构造式。
            // θ 要求方形且 size >= 11；主流程恒输出装饰后地图。
            // 运行时可用 --engine v3|theta 覆盖，无需改源码。
            public static bool UseTheta = false;
            public static int ThetaTier = 1;

            // P11：难度预设（生成阶段选入，不只是分析层诊断）。
            // 设为区间后，生成时直接产出该难度带的地图：
            //   v3 = 换派生 seed（seed + k*7919）重采样，首个 diff_score 落区间者胜出（预算 40 次）；
            //   θ  = 二分调制封边比例（P9 同一套），区间不可达则回退缺省封边。
            // 区间不可达不失败：回退缺省图 + 打印 NOT reachable。
            // null（缺省）= 完全旧行为；运行时用 --difficulty lo,hi 覆盖。
            public static double? DifficultyLo = null;
            public static double? DifficultyHi = null;

            // Text display
            public static bool ShowPath = true;
            public static char EmptySymbol = '.';
            public static char ObstacleSymbol = '#';
            public static char PathSymbol = '+';
            public static char SpawnSymbol = 'S';
            public static char ExitSymbol = 'E';
        }

        private readonly struct Point : IEquatable<Point>
        {
            public readonly int X;
            public readonly int Y;

            public Point(int x, int y) { X = x; Y = y; }
            public bool Equals(Point other) => X == other.X && Y == other.Y;
            public override bool Equals(object? obj) => obj is Point p && Equals(p);
            public override int GetHashCode() => HashCode.Combine(X, Y);
            public override string ToString() => $"({X},{Y})";
        }

        private readonly struct Direction : IEquatable<Direction>
        {
            public readonly int Dx;
            public readonly int Dy;

            public Direction(int dx, int dy) { Dx = dx; Dy = dy; }
            public bool Equals(Direction other) => Dx == other.Dx && Dy == other.Dy;
            public override bool Equals(object? obj) => obj is Direction d && Equals(d);
            public override int GetHashCode() => HashCode.Combine(Dx, Dy);

            public bool IsOpposite(Direction other)
                => Dx == -other.Dx && Dy == -other.Dy;

            public override string ToString()
            {
                if (Dx == 1) return "Right";
                if (Dx == -1) return "Left";
                if (Dy == 1) return "Down";
                return "Up";
            }
        }

        private sealed class PathMetrics
        {
            public int Turns;
            public List<int> SegmentLengths = new List<int>();
            public List<Direction> SegmentDirections = new List<Direction>();
            public List<int> UPathStartSegmentIndices = new List<int>();
            public List<int> UPathSizes = new List<int>();     // P8：每个 U 的 5 段总格数（与 Start 索引一一对应）
            public int UPathCount => UPathStartSegmentIndices.Count;
        }

        private sealed class MapData
        {
            public int Width;
            public int Height;
            public bool[,] Obstacles = new bool[0, 0];
            public Point Spawn;
            public Point Exit;
            public List<Point> Path = new List<Point>();
            public PathMetrics Metrics = new PathMetrics();
            public double PathRatio;
            public int AttemptsUsed;
            public int ObstacleCount;
            public int Seed;

            // 坐标级地形投影：属性不落第二套网格，始终由生成结果派生。
            // 障碍优先，保证不可跨越格不会被误判为可通行。
            public TerrainCell GetTerrainCell(int x, int y)
            {
                if (x < 0 || y < 0 || x >= Width || y >= Height)
                    throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x},{y}) is outside the map");

                if (Obstacles[x, y])
                    return TerrainCatalog.Describe(TerrainType.NaturalObstacle);
                if (x == Spawn.X && y == Spawn.Y)
                    return TerrainCatalog.Describe(TerrainType.Spawn);
                if (x == Exit.X && y == Exit.Y)
                    return TerrainCatalog.Describe(TerrainType.Exit);
                return TerrainCatalog.Describe(TerrainType.Empty);
            }
        }

        // 主流程 --cells 的输出路径；空串 = 不落盘（保持旧行为）。
        private static string CellExportPath = string.Empty;

        private static readonly Direction[] Directions =
        {
            new Direction(1, 0), new Direction(-1, 0),
            new Direction(0, 1), new Direction(0, -1)
        };

        private static void Main(string[] args)
        {
            // P3：分离度指标人工地图单元测试（优化指南 §7.1）。
            if (args.Length > 0 && args[0].TrimStart('-').Equals("sep-selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunSeparationSelfTest();
                WaitForExitKey();
                return;
            }

            // P8：U-pattern 数量与规模人工地图单元测试。
            if (args.Length > 0 && args[0].TrimStart('-').Equals("u-selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunUSelfTest();
                WaitForExitKey();
                return;
            }

            // P1 分析器批量模式：--analyze [--count N] [--nmax 1..4] [--seed N] [--combos N] [--csv path]
            if (args.Length > 0 && args[0].TrimStart('-').Equals("analyze", StringComparison.OrdinalIgnoreCase))
            {
                RunAnalyzerBatch(args);
                WaitForExitKey();
                return;
            }

            // 尺寸扫描：--sweep [--from 11] [--to 16] [--count 50]
            if (args.Length > 0 && args[0].TrimStart('-').Equals("sweep", StringComparison.OrdinalIgnoreCase))
            {
                RunSizeSweep(args);
                WaitForExitKey();
                return;
            }

            // P2a：θ 骨架：--theta [--from 11] [--to 16] [--count 50] [--tier 1|2] [--show]
            // P4：同批导出极简文本地图：--export <dir> [--bare]
            if (args.Length > 0 && args[0].TrimStart('-').Equals("theta", StringComparison.OrdinalIgnoreCase))
            {
                RunThetaBatch(args);
                WaitForExitKey();
                return;
            }

            // P5：回读校验：--load <file.map> | --load-dir <dir>
            string firstArg = args.Length > 0 ? args[0].TrimStart('-') : string.Empty;
            if (firstArg.Equals("load", StringComparison.OrdinalIgnoreCase)
                || firstArg.Equals("load-dir", StringComparison.OrdinalIgnoreCase))
            {
                RunLoadCheck(args);
                WaitForExitKey();
                return;
            }

            // 主流程参数：--engine v3|theta | --size N | --tier N | --seed N，覆盖 Settings 默认值。
            string argumentError = ApplyMainArguments(args);
            if (argumentError.Length > 0)
            {
                Console.WriteLine($"Invalid arguments: {argumentError}");
                Console.WriteLine("usage: [--engine v3|theta] [--size N] [--tier 1|2] [--seed N] [--difficulty LO,HI] [--cells PATH]");
                Environment.ExitCode = 1;
                return;
            }

            // 参数错误属于配置问题，打印可读消息并以非零码退出，不抛栈。
            try
            {
                ValidateSettings();
            }
            catch (ArgumentException error)
            {
                Console.WriteLine($"Invalid settings: {error.Message}");
                Environment.ExitCode = 1;
                return;
            }

            int seed = Settings.UseRandomSeed
                ? unchecked(Environment.TickCount * 31 + Guid.NewGuid().GetHashCode())
                : Settings.RandomSeed;

            var random = new Random(seed);

            Console.WriteLine("=== RMG V1.1 Tower Defense Map Generator ===");
            Console.WriteLine($"Map: {Settings.MapWidth} x {Settings.MapHeight}");
            Console.WriteLine($"Engine: {(Settings.UseTheta
                ? $"theta skeleton (tier {Settings.ThetaTier}, decorated)"
                : "v3 rejection sampling")}");
            Console.WriteLine($"Seed: {seed}");
            if (Settings.DifficultyLo.HasValue)
            {
                // P11：难度预设只在启用时出现，缺省 banner 保持原样（零漂移）。
                Console.WriteLine($"Difficulty target: [{Settings.DifficultyLo.GetValueOrDefault():F2}, "
                                  + $"{Settings.DifficultyHi.GetValueOrDefault():F2}] "
                                  + (Settings.UseTheta
                                      ? "(theta flank-ratio bisection)"
                                      : "(v3 derived-seed resampling, budget 40)"));
            }

            // v3 引擎的形态类参数在 θ 下不参与生成，直接明示，避免改了不起作用。
            if (Settings.UseTheta)
            {
                Console.WriteLine($"Note: v3-only settings (U-path target, gate distance, obstacle ratio,"
                                  + $" clusters) do not apply; theta uses target density {Profile.TargetDensity:F2}"
                                  + " as a soft target.");
            }
            else
            {
                Console.WriteLine($"Minimum gate-to-gate path length: {Settings.MinPathLengthBetweenGates} move(s)");
                Console.WriteLine($"Minimum natural min-cut: "
                                  + (Settings.MinNaturalMinCut > 0
                                      ? $"{Settings.MinNaturalMinCut} corridor(s)"
                                      : "unconstrained"));
                Console.WriteLine($"Target U-path count: {Settings.MinUPaths} - {Settings.MaxUPaths}");
            }

            Console.WriteLine();

            // P4.3：θ 与 v3 引擎只有生成阶段分派，后面的打印与分析层共用。
            MapData? map;
            ThetaResult? theta = null;

            if (Settings.UseTheta)
            {
                // P11：区间非空时 BuildThetaMainMap 内部二分封边比例（与 P9 同一套）。
                theta = BuildThetaMainMap(seed, Settings.DifficultyLo, Settings.DifficultyHi);
                map = theta.Map;
            }
            else
            {
                // P11：区间非空时走派生 seed 重采样；null 时与旧行为完全一致。
                map = GenerateMapForDifficulty(random, seed);
            }

            // 逐坐标输出：地图已确定后一次性落盘，v3 / θ 共用同一数据源。
            if (map != null && CellExportPath.Length > 0)
                WriteCellsCsv(CellExportPath, map);

            if (map == null)
            {
                Console.WriteLine("No candidate map passed all validation rules.");
                if (theta == null)
                {
                    Console.WriteLine($"Attempts made: {Settings.MaxGenerationAttempts}");
                    Console.WriteLine();
                    Console.WriteLine("Try these adjustments:");
                    Console.WriteLine("- Increase MapWidth / MapHeight.");
                    Console.WriteLine("- Increase ObstacleRatio moderately (for example 0.28-0.32).");
                    Console.WriteLine("- Reduce MinUPaths or MinTurns.");
                    Console.WriteLine("- Increase MaxSegmentLength.");
                    Console.WriteLine("- Widen MinPathRatio / MaxPathRatio.");
                    Console.WriteLine("- Increase MaxGenerationAttempts.");
                }
                else
                {
                    Console.WriteLine($"Reason: {theta.Reason}");
                    Console.WriteLine();
                    Console.WriteLine("Try these adjustments:");
                    Console.WriteLine($"- Increase MapWidth / MapHeight (theta needs a square map, size >= {Profile.ThetaMinSize}).");
                    Console.WriteLine($"- Set ThetaTier to 1 or {Profile.Tiers.Length}.");
                }

                // 生成失败必须反映到退出码，否则 CI 无法区分成功与空手而归。
                Environment.ExitCode = 1;
            }
            else if (theta != null)
            {
                // θ 的判据是拓扑（k=2 / ΔC / Killzone）与分析器的 n=1,2 改道结论，
                // 与 v3 引擎的形态判据（U 型 / 转数 / 路径比）无关，故不打印 v3 summary。
                PrintMap(map);
                PrintThetaSummary(theta, AnalyzeMap(map, Profile.AnalysisN, Profile.ComboCap, theta.BlockCost));
                PrintDifficulty(map, theta.DecorateFlank);
            }
            else
            {
                PrintMap(map);
                PrintSummary(map);
                PrintAnalysis(map, AnalyzeMap(map, Profile.AnalysisN, Profile.ComboCap, Profile.BlockCost));
                PrintDifficulty(map, derivedSeed: map.Seed, originalSeed: seed);
            }

            WaitForExitKey();
        }

        // 只解析主流程开关；未知参数一律报错，避免拼错后静默走默认值。
        // --size 同时设宽高（θ 只接受方形）；--engine 切换引擎，v3 引擎仍是默认。
        private static string ApplyMainArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i].TrimStart('-');

                if (key.Equals("engine", StringComparison.OrdinalIgnoreCase))
                {
                    string value = i + 1 < args.Length ? args[i + 1].TrimStart('-') : string.Empty;
                    if (value.Equals("v3", StringComparison.OrdinalIgnoreCase))
                        Settings.UseTheta = false;
                    else if (value.Equals("theta", StringComparison.OrdinalIgnoreCase))
                        Settings.UseTheta = true;
                    else
                        return "--engine needs v3 or theta";
                    i++;
                    continue;
                }

                if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int size))
                        return "--size needs an integer";
                    Settings.MapWidth = size;
                    Settings.MapHeight = size;
                    i++;
                    continue;
                }

                if (key.Equals("tier", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int tier))
                        return "--tier needs an integer";
                    Settings.ThetaTier = tier;
                    i++;
                    continue;
                }

                if (key.Equals("seed", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int seed))
                        return "--seed needs an integer";
                    Settings.UseRandomSeed = false;
                    Settings.RandomSeed = seed;
                    i++;
                    continue;
                }

                if (key.Equals("difficulty", StringComparison.OrdinalIgnoreCase))
                {
                    // P11：与 analyze 同款取值（lo,hi 或 lo:hi），生成阶段直接按区间选入。
                    string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
                    string[] parts = value.Split(':', ',');
                    if (parts.Length != 2
                        || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffLo)
                        || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffHi)
                        || diffLo < 0 || diffHi > 1 || diffLo > diffHi)
                    {
                        return "--difficulty needs 'lo,hi' with 0 <= lo <= hi <= 1";
                    }
                    Settings.DifficultyLo = diffLo;
                    Settings.DifficultyHi = diffHi;
                    i++;
                    continue;
                }

                if (key.Equals("cells", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || args[i + 1].Length == 0)
                        return "--cells needs an output file path";
                    CellExportPath = args[i + 1];
                    i++;
                    continue;
                }

                return $"unknown argument: {args[i]}";
            }

            return string.Empty;
        }

        private static void ValidateSettings()
        {
            if (Settings.MapWidth < 2 || Settings.MapHeight < 2)
                throw new ArgumentException("MapWidth and MapHeight must both be at least 2.");

            if (Settings.MinPathLengthBetweenGates < 1)
                throw new ArgumentException("MinPathLengthBetweenGates must be at least 1.");

            if (Settings.MinNaturalMinCut < 0 || Settings.MinNaturalMinCut > 4)
                throw new ArgumentException("MinNaturalMinCut must be between 0 and 4.");

            if (Settings.ObstacleRatio < 0 || Settings.ObstacleRatio > 0.60)
                throw new ArgumentException("ObstacleRatio must be between 0 and 0.60.");

            if (Settings.MinClusters < 0 || Settings.MaxClusters < Settings.MinClusters)
                throw new ArgumentException("Invalid cluster count range.");
            if (Settings.MinClusterSize < 1 || Settings.MaxClusterSize < Settings.MinClusterSize)
                throw new ArgumentException("Invalid cluster size range.");
            if (Settings.ClusterGrowthChance < 0 || Settings.ClusterGrowthChance > 1)
                throw new ArgumentException("ClusterGrowthChance must be between 0 and 1.");
            if (Settings.MinScatteredObstacles < 0 ||
                Settings.MaxScatteredObstacles < Settings.MinScatteredObstacles)
                throw new ArgumentException("Invalid scattered obstacle range.");

            if (Settings.MinTurns < 0 || Settings.MaxTurns < Settings.MinTurns)
                throw new ArgumentException("Invalid turn range.");
            if (Settings.MinSegmentLength < 1 ||
                Settings.MaxSegmentLength < Settings.MinSegmentLength)
                throw new ArgumentException("Invalid segment length range.");
            if (Settings.MinPathRatio < 1.0 || Settings.MaxPathRatio < Settings.MinPathRatio)
                throw new ArgumentException("Path ratios must satisfy 1.0 <= MinPathRatio <= MaxPathRatio.");

            if (Settings.MinUPaths < 0 || Settings.MaxUPaths < Settings.MinUPaths)
                throw new ArgumentException("Invalid U-path count range.");
            if (Settings.MaxGenerationAttempts < 1)
                throw new ArgumentException("MaxGenerationAttempts must be at least 1.");

            // θ 的档位索引直接落到 Profile.Tiers，越界会崩栈，故在入口拦下。
            if (Settings.UseTheta && (Settings.ThetaTier < 1 || Settings.ThetaTier > Profile.Tiers.Length))
                throw new ArgumentException(
                    $"ThetaTier must be between 1 and {Profile.Tiers.Length}.");

            // P11：难度预设区间（null = 不启用）。
            if (Settings.DifficultyLo.HasValue != Settings.DifficultyHi.HasValue)
                throw new ArgumentException("DifficultyLo and DifficultyHi must be set together.");
            if (Settings.DifficultyLo.HasValue
                && (Settings.DifficultyLo.Value < 0 || Settings.DifficultyHi!.Value > 1
                    || Settings.DifficultyLo.Value > Settings.DifficultyHi.Value))
                throw new ArgumentException("Difficulty range must satisfy 0 <= lo <= hi <= 1.");
        }

        // P11：v3 难度预设——沿派生 seed 序列重采样（seed + k*7919），
        // 首个 diff_score 落区间的图胜出；预算耗尽回退首张可生成图（不失败）。
        // 随机性全部由输入 seed 决定 ⇒ 同 seed + 同区间输出确定。
        private static MapData? GenerateMapForDifficulty(Random random, int seed)
        {
            if (!Settings.DifficultyLo.HasValue)
                return GenerateMap(random, seed);

            const int budget = 40;
            double lo = Settings.DifficultyLo.GetValueOrDefault();
            double hi = Settings.DifficultyHi.GetValueOrDefault();
            MapData? fallback = null;
            for (int k = 0; k < budget; k++)
            {
                int derived = unchecked(seed + k * 7919);
                MapData? candidate = GenerateMap(new Random(derived), derived);
                if (candidate == null) continue;

                fallback ??= candidate;
                double score = ComputeDifficulty(candidate).Score;
                if (score >= lo && score <= hi)
                    return candidate;
            }

            return fallback;
        }

        private static MapData? GenerateMap(Random random, int seed)
        {
            int width = Settings.MapWidth;
            int height = Settings.MapHeight;
            int obstacleBudget = (int)Math.Floor(width * height * Settings.ObstacleRatio);
            long genMark = GenerationProfiler.Mark();

            for (int attempt = 1; attempt <= Settings.MaxGenerationAttempts; attempt++)
            {
                // Pick both gates from the full boundary, without a relative-edge rule.
                // The two gates must be different cells.
                Point spawn = ChooseRandomBoundaryCell(random, width, height);
                Point exit = ChooseRandomBoundaryCell(random, width, height, spawn);
                int emptyPathLength = ManhattanDistance(spawn, exit);

                long sampleMark = GenerationProfiler.Mark();
                bool[,] obstacles = new bool[width, height];
                var protectedCells = new HashSet<Point> { spawn, exit };
                int placed = 0;

                int clusterCount = RandomInclusive(random, Settings.MinClusters, Settings.MaxClusters);
                for (int i = 0; i < clusterCount && placed < obstacleBudget; i++)
                {
                    int desiredSize = RandomInclusive(
                        random, Settings.MinClusterSize, Settings.MaxClusterSize);
                    desiredSize = Math.Min(desiredSize, obstacleBudget - placed);

                    if (desiredSize <= 0) break;
                    Point? center = FindRandomFreeCell(random, obstacles, protectedCells);
                    if (center == null) break;

                    List<Point> cluster = GrowCluster(
                        random, center.Value, desiredSize,
                        obstacles, protectedCells, width, height);

                    foreach (Point p in cluster)
                    {
                        if (placed >= obstacleBudget) break;
                        if (!obstacles[p.X, p.Y])
                        {
                            obstacles[p.X, p.Y] = true;
                            placed++;
                        }
                    }
                }

                int scatteredCount = RandomInclusive(
                    random, Settings.MinScatteredObstacles, Settings.MaxScatteredObstacles);

                for (int i = 0; i < scatteredCount && placed < obstacleBudget; i++)
                {
                    Point? p = FindRandomFreeCell(random, obstacles, protectedCells);
                    if (p == null) break;
                    obstacles[p.Value.X, p.Value.Y] = true;
                    placed++;
                }

                // Fill remaining budget with random cells so obstacles can meaningfully
                // alter the route. The total never exceeds ObstacleRatio's budget.
                while (placed < obstacleBudget)
                {
                    Point? p = FindRandomFreeCell(random, obstacles, protectedCells);
                    if (p == null) break;
                    obstacles[p.Value.X, p.Value.Y] = true;
                    placed++;
                }

                GenerationProfiler.AddStage("stage.sample", GenerationProfiler.Since(sampleMark));

                // Stage 1: basic reachability.
                long pathMark = GenerationProfiler.Mark();
                List<Point> path = FindShortestPath(
                    spawn, exit, obstacles, width, height);
                GenerationProfiler.AddStage("stage.pathfind", GenerationProfiler.Since(pathMark));
                if (path.Count == 0)
                {
                    GenerationProfiler.Reject("reject_unreachable");
                    continue;
                }

                // Enforce the configurable minimum shortest-path length in grid moves.
                if (path.Count - 1 < Settings.MinPathLengthBetweenGates)
                {
                    GenerationProfiler.Reject("reject_gate_distance");
                    continue;
                }

                // Stage 2: route shape and U-pattern validation.
                long shapeMark = GenerationProfiler.Mark();
                PathMetrics metrics = AnalyzePath(path);
                GenerationProfiler.AddStage("stage.shape", GenerationProfiler.Since(shapeMark));
                if (metrics.Turns < Settings.MinTurns || metrics.Turns > Settings.MaxTurns)
                {
                    GenerationProfiler.Reject("reject_turns");
                    continue;
                }

                if (metrics.SegmentLengths.Any(n =>
                    n < Settings.MinSegmentLength || n > Settings.MaxSegmentLength))
                {
                    GenerationProfiler.Reject("reject_segment");
                    continue;
                }

                double ratio = (double)(path.Count - 1) / Math.Max(1, emptyPathLength);
                if (ratio < Settings.MinPathRatio || ratio > Settings.MaxPathRatio)
                {
                    GenerationProfiler.Reject("reject_path_ratio");
                    continue;
                }

                if (metrics.UPathCount < Settings.MinUPaths ||
                    metrics.UPathCount > Settings.MaxUPaths)
                {
                    GenerationProfiler.Reject("reject_u_paths");
                    continue;
                }

                // S2: reject single-corridor maps. k = 1 means one cell controls
                // every route, so blocking that cell forces the toll path and no
                // combination can ever produce a true reroute.
                if (Settings.MinNaturalMinCut > 0)
                {
                    long cutMark = GenerationProfiler.Mark();
                    (int minCut, _) = ComputeMinVertexCut(
                        obstacles, width, height, spawn, exit);
                    GenerationProfiler.AddStage("stage.min_cut", GenerationProfiler.Since(cutMark));
                    if (minCut < Settings.MinNaturalMinCut)
                    {
                        GenerationProfiler.Reject("reject_min_cut");
                        continue;
                    }
                }

                GenerationProfiler.RecordGenerate(genMark, attempt);
                return new MapData
                {
                    Width = width,
                    Height = height,
                    Obstacles = obstacles,
                    Spawn = spawn,
                    Exit = exit,
                    Path = path,
                    Metrics = metrics,
                    PathRatio = ratio,
                    AttemptsUsed = attempt,
                    ObstacleCount = placed,
                    Seed = seed
                };
            }

            GenerationProfiler.RecordGenerate(genMark, -1);
            return null;
        }

        private static List<Point> GetBoundaryCells(int width, int height)
        {
            var boundary = new List<Point>();

            for (int x = 0; x < width; x++)
            {
                boundary.Add(new Point(x, 0));
                boundary.Add(new Point(x, height - 1));
            }

            for (int y = 1; y < height - 1; y++)
            {
                boundary.Add(new Point(0, y));
                boundary.Add(new Point(width - 1, y));
            }

            return boundary.Distinct().ToList();
        }

        private static Point ChooseRandomBoundaryCell(Random random, int width, int height)
        {
            List<Point> boundary = GetBoundaryCells(width, height);
            return boundary[random.Next(boundary.Count)];
        }

        private static Point ChooseRandomBoundaryCell(
            Random random, int width, int height, Point excluded)
        {
            List<Point> boundary = GetBoundaryCells(width, height)
                .Where(p => !p.Equals(excluded))
                .ToList();

            if (boundary.Count == 0)
                throw new InvalidOperationException(
                    "The map needs at least two boundary cells for separate gates.");

            // S1: bias the second gate toward the opposite side. Gates on the same
            // edge give a short empty-grid baseline, and any blocker then looks like
            // a huge detour. Candidates within the top quartile of distance stay in
            // the pool so the choice is still random.
            int farthest = boundary.Max(p => ManhattanDistance(p, excluded));
            int threshold = (int)Math.Ceiling(farthest * 0.75);
            List<Point> preferred = boundary
                .Where(p => ManhattanDistance(p, excluded) >= threshold)
                .ToList();

            List<Point> pool = preferred.Count > 0 ? preferred : boundary;
            return pool[random.Next(pool.Count)];
        }

        private static int ManhattanDistance(Point a, Point b)
            => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        private static List<Point> GrowCluster(
            Random random,
            Point center,
            int targetSize,
            bool[,] obstacles,
            HashSet<Point> protectedCells,
            int width,
            int height)
        {
            var cluster = new List<Point> { center };
            var clusterSet = new HashSet<Point> { center };
            var frontier = new List<Point> { center };

            while (cluster.Count < targetSize && frontier.Count > 0)
            {
                int index = random.Next(frontier.Count);
                Point current = frontier[index];
                frontier.RemoveAt(index);

                var candidates = GetNeighbors(current, width, height)
                    .Where(p => !obstacles[p.X, p.Y])
                    .Where(p => !protectedCells.Contains(p))
                    .Where(p => !clusterSet.Contains(p))
                    .OrderBy(_ => random.Next())
                    .ToList();

                foreach (Point next in candidates)
                {
                    if (cluster.Count >= targetSize) break;

                    if (random.NextDouble() <= Settings.ClusterGrowthChance)
                    {
                        cluster.Add(next);
                        clusterSet.Add(next);
                        frontier.Add(next);
                    }
                }
            }

            return cluster;
        }

        private static Point? FindRandomFreeCell(
            Random random,
            bool[,] obstacles,
            HashSet<Point> protectedCells)
        {
            int width = obstacles.GetLength(0);
            int height = obstacles.GetLength(1);
            var candidates = new List<Point>();

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var p = new Point(x, y);
                if (!obstacles[x, y] && !protectedCells.Contains(p))
                    candidates.Add(p);
            }

            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        // ============================================================
        // P0 契约：全项目唯一寻路实现（生成器 / 分析器 / 游戏运行时共用）
        // 成本场 Dijkstra，tie-break 固定为 (cost, y, x) 字典序，
        // 因此「当前路线」唯一、可复现，分析器证书与实机一致。
        // 自然格代价 1；tolledCells 中的格代价 = tollCost（软代价阻挡，仍可通行）。
        // ============================================================
        private static List<Point> FindPath(
            Point start,
            Point goal,
            bool[,] obstacles,
            int width,
            int height,
            HashSet<Point>? tolledCells,
            int tollCost,
            out int pathCost)
        {
            var best = new Dictionary<Point, int> { [start] = 0 };
            var previous = new Dictionary<Point, Point>();
            var queue = new PriorityQueue<Point, (int Cost, int Y, int X)>();

            queue.Enqueue(start, (0, start.Y, start.X));

            while (queue.TryDequeue(out Point current, out (int Cost, int Y, int X) priority))
            {
                if (priority.Cost > best[current]) continue;
                if (current.Equals(goal)) break;

                foreach (Direction direction in Directions)
                {
                    int x = current.X + direction.Dx;
                    int y = current.Y + direction.Dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    if (obstacles[x, y]) continue;

                    var next = new Point(x, y);
                    int step = tolledCells != null && tolledCells.Contains(next) ? tollCost : 1;
                    int candidate = priority.Cost + step;

                    if (best.TryGetValue(next, out int known) && known <= candidate) continue;

                    best[next] = candidate;
                    previous[next] = current;
                    queue.Enqueue(next, (candidate, next.Y, next.X));
                }
            }

            if (!best.TryGetValue(goal, out pathCost))
            {
                pathCost = int.MaxValue;
                return new List<Point>();
            }

            var path = new List<Point> { goal };
            Point cursor = goal;
            while (!cursor.Equals(start))
            {
                cursor = previous[cursor];
                path.Add(cursor);
            }

            path.Reverse();
            return path;
        }

        // 生成阶段入口：无软代价，等价于等权最短路。
        private static List<Point> FindShortestPath(
            Point start,
            Point goal,
            bool[,] obstacles,
            int width,
            int height)
            => FindPath(start, goal, obstacles, width, height, null, 0, out _);

        private static PathMetrics AnalyzePath(List<Point> path)
        {
            var result = new PathMetrics();
            if (path.Count <= 1) return result;

            // Convert cell-by-cell path into straight segments.
            Direction currentDirection = new Direction(
                path[1].X - path[0].X,
                path[1].Y - path[0].Y);
            int currentLength = 1;

            for (int i = 2; i < path.Count; i++)
            {
                var nextDirection = new Direction(
                    path[i].X - path[i - 1].X,
                    path[i].Y - path[i - 1].Y);

                if (nextDirection.Equals(currentDirection))
                {
                    currentLength++;
                }
                else
                {
                    result.SegmentDirections.Add(currentDirection);
                    result.SegmentLengths.Add(currentLength);
                    result.Turns++;
                    currentDirection = nextDirection;
                    currentLength = 1;
                }
            }

            result.SegmentDirections.Add(currentDirection);
            result.SegmentLengths.Add(currentLength);

            // Count non-overlapping U-pattern groups:
            // 5 consecutive straight segments = 4 adjacent turns,
            // and the first and last segment directions must be opposite.
            // After counting a group, continue after its 5 segments so the same
            // stretch of route cannot be counted multiple times.
            int segmentIndex = 0;
            while (segmentIndex + 4 < result.SegmentDirections.Count)
            {
                Direction first = result.SegmentDirections[segmentIndex];
                Direction last = result.SegmentDirections[segmentIndex + 4];

                if (first.IsOpposite(last))
                {
                    result.UPathStartSegmentIndices.Add(segmentIndex);
                    int size = 0;
                    for (int k = segmentIndex; k < segmentIndex + 5; k++)
                        size += result.SegmentLengths[k];
                    result.UPathSizes.Add(size);   // P8：U 规模 = 5 段总格数
                    segmentIndex += 5;
                }
                else
                {
                    segmentIndex++;
                }
            }

            return result;
        }

        private static IEnumerable<Point> GetNeighbors(Point p, int width, int height)
        {
            foreach (Direction d in Directions)
            {
                int x = p.X + d.Dx;
                int y = p.Y + d.Dy;

                if (x >= 0 && x < width && y >= 0 && y < height)
                    yield return new Point(x, y);
            }
        }

        private static int RandomInclusive(Random random, int min, int max)
            => random.Next(min, max + 1);

        private static void PrintMap(MapData map)
        {
            var pathSet = new HashSet<Point>(map.Path);

            Console.WriteLine("Legend: S = spawn, E = exit, # = natural obstacle, + = selected shortest path, . = empty");
            Console.WriteLine();

            Console.Write("    ");
            for (int x = 0; x < map.Width; x++) Console.Write($"{x,2} ");
            Console.WriteLine();

            Console.Write("   +");
            Console.Write(new string('-', map.Width * 3));
            Console.WriteLine("+");

            for (int y = 0; y < map.Height; y++)
            {
                Console.Write($"{y,2} |");

                for (int x = 0; x < map.Width; x++)
                {
                    var p = new Point(x, y);
                    char symbol;

                    if (p.Equals(map.Spawn)) symbol = Settings.SpawnSymbol;
                    else if (p.Equals(map.Exit)) symbol = Settings.ExitSymbol;
                    else if (map.Obstacles[x, y]) symbol = Settings.ObstacleSymbol;
                    else if (Settings.ShowPath && pathSet.Contains(p)) symbol = Settings.PathSymbol;
                    else symbol = Settings.EmptySymbol;

                    Console.Write($" {symbol} ");
                }

                Console.WriteLine("|");
            }

            Console.Write("   +");
            Console.Write(new string('-', map.Width * 3));
            Console.WriteLine("+");
        }

        // ============================================================
        // 逐坐标 CSV：x,y,terrainType,traversalType,cost
        // 可通行格 cost 为有效数值；不可跨越格 cost 写 null。
        // ============================================================
        private static string RenderCellsCsv(MapData map)
        {
            var sb = new StringBuilder();
            sb.Append("x,y,terrainType,traversalType,cost\n");

            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                TerrainCell cell = map.GetTerrainCell(x, y);
                sb.Append(x.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(y.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(cell.TerrainType).Append(',')
                  .Append(cell.TraversalType).Append(',')
                  .Append(cell.Cost.HasValue
                      ? cell.Cost.Value.ToString(CultureInfo.InvariantCulture)
                      : "null")
                  .Append('\n');
            }

            return sb.ToString();
        }

        private static void WriteCellsCsv(string path, MapData map)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllText(full, RenderCellsCsv(map), new UTF8Encoding(false));
            Console.WriteLine($"Cell map written: {full}");
        }

        private static void PrintSummary(MapData map)
        {
            double actualRatio = (double)map.ObstacleCount / (map.Width * map.Height);

            Console.WriteLine();
            Console.WriteLine("=== Validation summary ===");
            Console.WriteLine($"Spawn: {map.Spawn}");
            Console.WriteLine($"Exit: {map.Exit}");
            Console.WriteLine($"Natural obstacles: {map.ObstacleCount}/{map.Width * map.Height} ({actualRatio:P1})");
            Console.WriteLine($"Shortest path moves: {map.Path.Count - 1}");
            Console.WriteLine($"Required minimum gate-to-gate path moves: {Settings.MinPathLengthBetweenGates}");
            Console.WriteLine($"Empty-map shortest path moves: {ManhattanDistance(map.Spawn, map.Exit)}");
            Console.WriteLine($"Path ratio: {map.PathRatio:F2}");
            Console.WriteLine($"Turn count: {map.Metrics.Turns}");
            Console.WriteLine($"Straight segment lengths: {string.Join(", ", map.Metrics.SegmentLengths)}");
            Console.WriteLine($"U-pattern count: {map.Metrics.UPathCount}");
            Console.WriteLine($"Accepted attempt: {map.AttemptsUsed}/{Settings.MaxGenerationAttempts}");
            Console.WriteLine("Reachability validation: PASS");
            Console.WriteLine("Path-shape validation: PASS");
            Console.WriteLine("U-pattern validation: PASS");

            for (int i = 0; i < map.Metrics.UPathStartSegmentIndices.Count; i++)
            {
                int start = map.Metrics.UPathStartSegmentIndices[i];
                var dirs = map.Metrics.SegmentDirections.Skip(start).Take(5)
                    .Select(d => d.ToString());
                Console.WriteLine($"U-pattern {i + 1}: segments {start + 1}-{start + 5}; directions = {string.Join(" -> ", dirs)}");
            }
        }

        // P11：主流程单图恒打印难度块。无目标时作参考口径（含三维明细与建议用法），
        // 设了目标时追加命中判定与调制留痕（封边比例 / 派生 seed）。
        private static void PrintDifficulty(MapData map, double? flank = null,
            int? derivedSeed = null, int originalSeed = 0)
        {
            var diff = ComputeDifficulty(map);
            bool hasTarget = Settings.DifficultyLo.HasValue && Settings.DifficultyHi.HasValue;
            double lo = Settings.DifficultyLo.GetValueOrDefault();
            double hi = Settings.DifficultyHi.GetValueOrDefault();
            bool inRange = diff.Score >= lo && diff.Score <= hi;

            Console.WriteLine();
            Console.WriteLine(hasTarget
                ? $"=== Difficulty (target {lo:F2}-{hi:F2}) ==="
                : "=== Difficulty ===");
            Console.WriteLine($"d1 path {diff.D1:F3} | d2 open {diff.D2Open:F3} | "
                              + $"d3 U count {diff.UCount} / size {diff.USize}");
            Console.WriteLine(hasTarget
                ? $"diff_score: {diff.Score:F3}  -> {(inRange ? "IN RANGE" : "NOT reachable")}"
                : $"diff_score: {diff.Score:F3}  (reference only; add --difficulty LO,HI to target a band)");
            if (hasTarget && flank.HasValue)
                Console.WriteLine($"Flank ratio used: {flank.Value:F3}"
                                  + (flank.Value >= 1.0 ? " (default)" : string.Empty));
            if (hasTarget && derivedSeed.HasValue)
                Console.WriteLine($"Derived seed: {derivedSeed.Value} (+{derivedSeed.Value - originalSeed})");
        }
    }
}
