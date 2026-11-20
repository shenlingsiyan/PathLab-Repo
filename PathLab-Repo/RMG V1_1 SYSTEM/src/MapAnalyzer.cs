using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RMG_V1_1
{
    internal static partial class Program
    {
        // ============================================================
        // P1 策略性分析器（软代价模型）
        // 规则约定：
        //   1. 自然障碍 = 硬墙（不可通行），由生成器产出；
        //   2. 玩家放置 = 软代价，格子仍可通行，但代价 = W（一格一物，不可叠加）；
        //   3. 因此永远不会「封死」，S→E 只要自然地图连通就恒可达；
        //   4. n 按格子数计。
        // 分析器只测量与出证书，不改变地图生成逻辑。
        // ============================================================

        private const int InfiniteCapacity = 1 << 20;
        private const int ProbeBlockCost = 10000;           // 用于探测「另一条走廊」的极高代价

        // P3：判据阈值统一来自 Profile（数值与旧常量逐位相同）。
        // P6：W 不再走静态字段——每图独立（v3 用 --w，θ 用按图拟合值），由调用方
        // 作为参数传入 AnalyzeMap，避免批量并行时的跨图竞态。

        private sealed class DinicEdge
        {
            public int To;
            public int Capacity;
            public int Reverse;
        }

        // 节点拆分 + Dinic 最大流：用于自然地图的顶点最小割（仅作诊断）。
        private sealed class Dinic
        {
            private readonly List<DinicEdge>[] _graph;
            private readonly int[] _level;
            private readonly int[] _iter;

            public Dinic(int nodeCount)
            {
                _graph = new List<DinicEdge>[nodeCount];
                for (int i = 0; i < nodeCount; i++) _graph[i] = new List<DinicEdge>();
                _level = new int[nodeCount];
                _iter = new int[nodeCount];
            }

            public void AddEdge(int from, int to, int capacity)
            {
                _graph[from].Add(new DinicEdge
                {
                    To = to,
                    Capacity = capacity,
                    Reverse = _graph[to].Count
                });
                _graph[to].Add(new DinicEdge
                {
                    To = from,
                    Capacity = 0,
                    Reverse = _graph[from].Count - 1
                });
            }

            public int MaxFlow(int source, int sink)
            {
                int flow = 0;
                while (BuildLevels(source, sink))
                {
                    Array.Clear(_iter, 0, _iter.Length);
                    int pushed;
                    while ((pushed = SendFlow(source, sink, int.MaxValue)) > 0)
                        flow += pushed;
                }
                return flow;
            }

            private bool BuildLevels(int source, int sink)
            {
                Array.Fill(_level, -1);
                _level[source] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(source);

                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (DinicEdge edge in _graph[u])
                    {
                        if (edge.Capacity <= 0 || _level[edge.To] >= 0) continue;
                        _level[edge.To] = _level[u] + 1;
                        queue.Enqueue(edge.To);
                    }
                }

                return _level[sink] >= 0;
            }

            private int SendFlow(int u, int sink, int limit)
            {
                if (u == sink) return limit;

                for (; _iter[u] < _graph[u].Count; _iter[u]++)
                {
                    DinicEdge edge = _graph[u][_iter[u]];
                    if (edge.Capacity <= 0 || _level[edge.To] != _level[u] + 1) continue;

                    int pushed = SendFlow(edge.To, sink, Math.Min(limit, edge.Capacity));
                    if (pushed <= 0) continue;

                    edge.Capacity -= pushed;
                    _graph[edge.To][edge.Reverse].Capacity += pushed;
                    return pushed;
                }

                return 0;
            }

            public bool[] ReachableFrom(int source)
            {
                var seen = new bool[_graph.Length];
                var queue = new Queue<int>();
                seen[source] = true;
                queue.Enqueue(source);

                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (DinicEdge edge in _graph[u])
                    {
                        if (edge.Capacity <= 0 || seen[edge.To]) continue;
                        seen[edge.To] = true;
                        queue.Enqueue(edge.To);
                    }
                }

                return seen;
            }
        }

        private struct SwitchSample
        {
            public int Moves;
            public int Cost;
            public bool Tolled;
            public double Overlap;
            public double HalfSeparation;
            public double CentroidShift;
            public double Delta;
            public double KillzoneGain;
            public double Score;
            public List<Point> Blockers;
            public List<Point> NewPath;
        }

        private sealed class SwitchReport
        {
            public int N;
            public int Candidates;          // 主路内部可封锁格数
            public long CombosExpected;     // C(Candidates, N)，饱和到 long.MaxValue
            public int Combos;
            public bool CapHit;             // 枚举被 combination 上限截断
            public int Tolled;      // 敌人硬扛罚金，仍踩封锁格
            public int Switched;    // 敌人完全绕开封锁格
            public int EqualCost;   // 通行总代价与原路相同
            public int Passing;
            public int PassingAdaptive;   // P2：n 自适应增幅窗口 + 半分离可用档
            public SwitchSample? Best;
        }

        private sealed class MapAnalysis
        {
            public bool HasPath;
            public int PathMoves;
            public int BaseCost;
            public int BlockCost;               // P6：本图分析用的 W（随图携带，供展示层读取）
            public bool PrintedMatchesCanonical;
            public int MinCut;
            public List<Point> Choke = new List<Point>();
            public double Killzone;
            public int AlternativeCost = -1;
            public int SwitchPrediction = -1;   // 理论所需封锁格数，-1 = 不存在真正独立的替代走廊
            public List<SwitchReport> Reports = new List<SwitchReport>();
        }

        // ---------------- 基础图操作 ----------------

        // 寻路统一走 MapGenerator.cs 里的 FindPath（P0 契约）：
        // 成本场 Dijkstra + (cost, y, x) 字典序 tie-break，生成器与分析器共用同一份实现。

        // 自然地图的顶点最小割 = 最大顶点不相交路径数（Menger）。
        // 软代价规则下它不再决定「能否封死」，只作为「有几条独立走廊」的诊断。
        private static (int Flow, List<Point> Cut) ComputeMinVertexCut(
            bool[,] obstacles, int width, int height, Point spawn, Point exit)
        {
            var cells = new List<Point>();
            var indices = new Dictionary<Point, int>();

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (obstacles[x, y]) continue;
                var cell = new Point(x, y);
                indices[cell] = cells.Count;
                cells.Add(cell);
            }

            int count = cells.Count;
            var dinic = new Dinic(count * 2);

            for (int i = 0; i < count; i++)
            {
                int capacity = (cells[i].Equals(spawn) || cells[i].Equals(exit))
                    ? InfiniteCapacity
                    : 1;
                dinic.AddEdge(2 * i, 2 * i + 1, capacity);
            }

            for (int i = 0; i < count; i++)
            {
                Point cell = cells[i];
                foreach (Direction direction in Directions)
                {
                    int x = cell.X + direction.Dx;
                    int y = cell.Y + direction.Dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    if (obstacles[x, y]) continue;

                    dinic.AddEdge(2 * i + 1, 2 * indices[new Point(x, y)], InfiniteCapacity);
                }
            }

            int source = 2 * indices[spawn];
            int sink = 2 * indices[exit] + 1;
            int flow = dinic.MaxFlow(source, sink);

            var cut = new List<Point>();
            if (flow > 0)
            {
                bool[] reachable = dinic.ReachableFrom(source);
                for (int i = 0; i < count; i++)
                {
                    if (reachable[2 * i] && !reachable[2 * i + 1]) cut.Add(cells[i]);
                }
            }

            return (flow, cut);
        }

        // ---------------- 度量 ----------------

        private static double JaccardOverlap(List<Point> a, List<Point> b)
        {
            var setA = new HashSet<Point>(a);
            var setB = new HashSet<Point>(b);
            int intersection = setA.Count(cell => setB.Contains(cell));
            int union = setA.Count + setB.Count - intersection;
            return union == 0 ? 1.0 : (double)intersection / union;
        }

        // ---------------- 难度诊断（P8，只读） ----------------

        // 方向约定：分值越高 = 对玩家越难防守（敌人路程少、路径周边开阔、U 型少）。
        // 与判据体系（通过率/ΔC = 玩家可利用性）互补两极，不参与任何判据与生成逻辑。
        // d1 路程：path_ratio 越短越难，按 [MinPathRatio, MaxPathRatio] 线性归一后取反；
        // d2 开阔：主路每格 ±1 八邻（界内、非主路格）中空格占比，越空旷敌人绕行/封锁自由度越大；
        // d3 U 型：生成器 U-pattern 零件（5 段首尾反向组）数量与总规模，越少越小越难；θ 构造恒 0；
        // diff_score 仅参考口径：MapProfile.Difficulty* 均等加权，D4 诱导维由现有专列承载不重复。
        private static (double D1, double D2Open, int UCount, int USize, double Score) ComputeDifficulty(
            MapData map)
        {
            double span = Settings.MaxPathRatio - Settings.MinPathRatio;
            double d1 = span <= 0 ? 0 : (Settings.MaxPathRatio - map.PathRatio) / span;
            d1 = Math.Clamp(d1, 0.0, 1.0);

            var pathSet = new HashSet<Point>(map.Path);
            long empty = 0;
            long solid = 0;
            foreach (Point cell in map.Path)
            {
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int x = cell.X + dx;
                    int y = cell.Y + dy;
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    var neighbor = new Point(x, y);
                    if (pathSet.Contains(neighbor)) continue;
                    if (map.Obstacles[x, y]) solid++;
                    else empty++;
                }
            }

            double d2 = (empty + solid) == 0 ? 1.0 : (double)empty / (empty + solid);

            int uCount = map.Metrics.UPathCount;
            int uSize = map.Metrics.UPathSizes.Sum();
            const double uSizeCap = 30.0;   // MaxUPaths=2 × 单 U 至多 5 段 × MaxSegmentLength=6
            double uCountScore = uCount == 0 ? 1.0 : (uCount == 1 ? 0.5 : 0.0);
            double uSizeScore = 1.0 - Math.Clamp(uSize / uSizeCap, 0.0, 1.0);
            double d3 = 0.5 * uCountScore + 0.5 * uSizeScore;

            double score = Profile.DifficultyWeightD1 * d1
                         + Profile.DifficultyWeightD2 * d2
                         + Profile.DifficultyWeightD3 * d3;
            return (d1, d2, uCount, uSize, score);
        }

        // P3：路线空间分离度（§7.1）。从旧路多源 BFS（4 连通，障碍格不可穿越——敌人真实绕行
        // 语义）得到步距场，对新路统计：最小步距 / ≤2 步占比 / 中位步距 / 最长连续贴边段(≤1 步)。
        // 只对 best 样本计算，不进入组合枚举热路径。中位数在偶数长度时取左中位。
        private static (int Min, double Adj2, int Median, int Run) ComputeSeparation(
            List<Point> basePath, List<Point> newPath, bool[,] obstacles, int width, int height)
        {
            var dist = new int[width, height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                dist[x, y] = -1;

            var queue = new Queue<Point>();
            foreach (Point cell in basePath)
            {
                if (dist[cell.X, cell.Y] != -1) continue;
                dist[cell.X, cell.Y] = 0;
                queue.Enqueue(cell);
            }

            while (queue.Count > 0)
            {
                Point current = queue.Dequeue();
                foreach (Direction direction in Directions)
                {
                    int x = current.X + direction.Dx;
                    int y = current.Y + direction.Dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    if (obstacles[x, y] || dist[x, y] != -1) continue;
                    dist[x, y] = dist[current.X, current.Y] + 1;
                    queue.Enqueue(new Point(x, y));
                }
            }

            var distances = new List<int>();
            int run = 0;
            int bestRun = 0;
            foreach (Point cell in newPath)
            {
                int d = dist[cell.X, cell.Y];
                if (d < 0) continue;   // 防御：不连通时跳过（S→E 双路必连通，理论不可达）

                distances.Add(d);
                if (d <= 1)
                {
                    run++;
                    if (run > bestRun) bestRun = run;
                }
                else
                {
                    run = 0;
                }
            }

            if (distances.Count == 0) return (0, 0.0, 0, 0);

            distances.Sort();
            return (distances[0],
                    (double)distances.Count(d => d <= 2) / distances.Count,
                    distances[(distances.Count - 1) / 2],
                    bestRun);
        }

        // P8：U-pattern 数量与规模人工案例（AnalyzePath 是纯路径几何函数，无需障碍地图）。
        private static void RunUSelfTest()
        {
            int failures = 0;

            void Check(string name, bool ok, string detail)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
                if (!ok) failures++;
            }

            // 案例 A：5 段 U（首 R3 尾 L2 相反）→ (0,0)→(3,0)→(3,3)→(2,3)→(2,2)→(0,2)。
            // 手算：UPathCount=1，规模 = 3+3+1+1+2 = 10。
            var uPath = new List<Point>
            {
                new Point(0, 0), new Point(1, 0), new Point(2, 0), new Point(3, 0),
                new Point(3, 1), new Point(3, 2), new Point(3, 3),
                new Point(2, 3), new Point(2, 2), new Point(1, 2), new Point(0, 2)
            };
            PathMetrics uMetrics = AnalyzePath(uPath);
            Check("u_count", uMetrics.UPathCount == 1, $"expect 1, got {uMetrics.UPathCount}");
            Check("u_size",
                uMetrics.UPathSizes.Count == 1 && uMetrics.UPathSizes[0] == 10,
                $"expect [10], got [{string.Join(",", uMetrics.UPathSizes)}]");

            // 案例 B：直线 1 段，无 U。
            var linePath = new List<Point>
            {
                new Point(0, 0), new Point(1, 0), new Point(2, 0), new Point(3, 0), new Point(4, 0)
            };
            PathMetrics lineMetrics = AnalyzePath(linePath);
            Check("line_no_u",
                lineMetrics.UPathCount == 0 && lineMetrics.UPathSizes.Count == 0,
                $"expect empty, got count={lineMetrics.UPathCount}");

            Console.WriteLine(failures == 0
                ? "u-selftest: 3/3 PASS"
                : $"u-selftest: {failures} FAIL");
        }

        // P3 §7.1 人工地图单元测试：锁定分离度语义，能区分「真分离 / 贴边并行 / 共享格 / 障碍绕行」。
        private static void RunSeparationSelfTest()
        {
            int failures = 0;

            void Check(string name, bool ok, string detail)
            {
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
                if (!ok) failures++;
            }

            // 案例 A 真分离：6x5 全空，旧路顶行，新路底行 → 全程步距 4。
            var empty = new bool[6, 5];
            var baseA = Enumerable.Range(0, 6).Select(x => new Point(x, 0)).ToList();
            var farA = Enumerable.Range(0, 6).Select(x => new Point(x, 4)).ToList();
            var a = ComputeSeparation(baseA, farA, empty, 6, 5);
            Check("A separated", a.Min == 4 && a.Median == 4 && a.Run == 0 && Math.Abs(a.Adj2) < 1e-9,
                  $"min={a.Min} adj2={a.Adj2:F3} med={a.Median} run={a.Run}");

            // 案例 B 贴边并行：新路紧贴旧路下一行 → 全程步距 1，overlap=0 也逃不过本指标。
            var nearB = Enumerable.Range(0, 6).Select(x => new Point(x, 1)).ToList();
            var b = ComputeSeparation(baseA, nearB, empty, 6, 5);
            Check("B parallel", b.Min == 1 && b.Adj2 > 0.999 && b.Median == 1 && b.Run == 6,
                  $"min={b.Min} adj2={b.Adj2:F3} med={b.Median} run={b.Run}");

            // 案例 C 共享格：新路先沿旧路走 4 格再分叉向下 → min=0，贴边段 = 共享段 + 分叉首格。
            var shareC = Enumerable.Range(0, 4).Select(x => new Point(x, 0)).ToList();
            shareC.AddRange(new[]
            {
                new Point(3, 1), new Point(3, 2), new Point(3, 3), new Point(3, 4),
                new Point(4, 4), new Point(5, 4)
            });
            var c = ComputeSeparation(baseA, shareC, empty, 6, 5);
            Check("C shared", c.Min == 0 && Math.Abs(c.Adj2 - 0.6) < 1e-9 && c.Median == 1 && c.Run == 5,
                  $"min={c.Min} adj2={c.Adj2:F3} med={c.Median} run={c.Run}");

            // 案例 D 障碍绕行：两行之间夹一排墙（中段封闭、两端开口）→ 几何相邻但步距被墙抬升。
            var walled = new bool[6, 5];
            for (int x = 1; x <= 4; x++) walled[x, 1] = true;
            var midD = Enumerable.Range(0, 6).Select(x => new Point(x, 2)).ToList();
            var d = ComputeSeparation(baseA, midD, walled, 6, 5);
            Check("D wall detour", d.Min == 2 && d.Median == 3 && d.Run == 0,
                  $"min={d.Min} adj2={d.Adj2:F3} med={d.Median} run={d.Run}");

            Console.WriteLine($"sep-selftest: {(failures == 0 ? "all checks passed" : $"{failures} check(s) FAILED")}");
            if (failures > 0) Environment.ExitCode = 1;
        }

        private static Point Centroid(List<Point> path)
        {
            double x = 0;
            double y = 0;
            foreach (Point p in path)
            {
                x += p.X;
                y += p.Y;
            }
            return new Point((int)Math.Round(x / path.Count), (int)Math.Round(y / path.Count));
        }

        private static double CentroidShift(List<Point> a, List<Point> b)
        {
            Point ca = Centroid(a);
            Point cb = Centroid(b);
            double dx = cb.X - ca.X;
            double dy = cb.Y - ca.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // 半场分离：只统计「对方路径没走过」的格子落在自己一侧的比例。
        // θ 骨架的共享茎段同时属于两条路线，若计入会把分离度错误地压到 0.5 附近。
        private static double HalfSeparation(List<Point> a, List<Point> b)
        {
            Point ca = Centroid(a);
            Point cb = Centroid(b);
            double dx = cb.X - ca.X;
            double dy = cb.Y - ca.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 1e-9) return 0;

            double mx = (ca.X + cb.X) / 2.0;
            double my = (ca.Y + cb.Y) / 2.0;

            var setA = new HashSet<Point>(a);
            var setB = new HashSet<Point>(b);
            var exclusiveA = a.Where(p => !setB.Contains(p)).ToList();
            var exclusiveB = b.Where(p => !setA.Contains(p)).ToList();

            if (exclusiveA.Count == 0 || exclusiveB.Count == 0) return 0;

            int ownA = exclusiveA.Count(p => (p.X - mx) * dx + (p.Y - my) * dy <= 0);
            int ownB = exclusiveB.Count(p => (p.X - mx) * dx + (p.Y - my) * dy > 0);

            return Math.Min((double)ownA / exclusiveA.Count, (double)ownB / exclusiveB.Count);
        }

        // 火力覆盖：路径每格周围 2 格内、可建造（非障碍、非路径、非门）的空格数，按路径长度平均。
        private static double ComputeKillzone(List<Point> path, MapData map)
        {
            var pathSet = new HashSet<Point>(path);

            int slots = 0;
            foreach (Point cell in path)
            {
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int x = cell.X + dx;
                    int y = cell.Y + dy;
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    if (map.Obstacles[x, y]) continue;

                    var candidate = new Point(x, y);
                    if (pathSet.Contains(candidate)) continue;
                    if (candidate.Equals(map.Spawn) || candidate.Equals(map.Exit)) continue;
                    slots++;
                }
            }

            return path.Count == 0 ? 0 : (double)slots / path.Count;
        }

        private static bool PassesOtherHalf(
            bool switched, double overlap, double halfSeparation, double delta)
            => switched
               && overlap <= Profile.OverlapLimit
               && halfSeparation >= Profile.HalfSeparationLimit
               && delta >= Profile.DeltaMin
               && delta <= Profile.DeltaMax;

        // P2：n 自适应增幅上限。封锁格越多，绕行必然更长，用固定 35% 会把 n>=3 的
        // 绝大多数候选判死（实测 n=3/4 有 48%-49% 地图超限）。每多封锁一格放宽 5pp。
        private const double DeltaMaxStepPerN = 0.05;

        // P2：半分离双档。0.70 保持为「优质」档，0.55 为「可用」档。
        private const double UsableHalfSeparationLimit = 0.55;

        private static double AdaptiveDeltaMax(int n)
            => Profile.DeltaMax + DeltaMaxStepPerN * Math.Max(0, n - 1);

        // P0：地图级三态判定，以严格判据（other-half）的合格组合数为准。
        // 截断且未发现合格组合只能得出「未知」，不得计入普通失败。
        private static string MapPassStatus(int passingCombinations, bool capHit)
        {
            if (passingCombinations > 0) return "pass";
            return capHit ? "unknown_truncated" : "fail_complete";
        }

        private static bool PassesAdaptive(
            bool switched, double overlap, double halfSeparation, double delta, int n)
            => switched
               && overlap <= Profile.OverlapLimit
               && halfSeparation >= UsableHalfSeparationLimit
               && delta >= Profile.DeltaMin
               && delta <= AdaptiveDeltaMax(n);

        private static string HalfSeparationTier(double halfSeparation)
            => halfSeparation >= Profile.HalfSeparationLimit ? "premium"
               : halfSeparation >= UsableHalfSeparationLimit ? "usable"
               : "fail";

        private static double ScoreSwitch(
            bool switched, double overlap, double halfSeparation, double delta, double killzoneGain)
        {
            if (!switched) return 0;

            double deltaScore = Math.Clamp(delta / Profile.DeltaMax, 0, 1);
            double killzoneScore = Math.Clamp(killzoneGain, 0, 1);
            return 0.35 * halfSeparation
                   + 0.30 * (1.0 - overlap)
                   + 0.25 * deltaScore
                   + 0.10 * killzoneScore;
        }

        // 组合总数（饱和到 long.MaxValue），用于判定枚举是否被上限截断。
        private static long CombinationCount(int total, int pick)
        {
            if (pick < 0 || total < pick) return 0;

            long result = 1;
            for (int i = 1; i <= pick; i++)
            {
                result = result * (total - pick + i) / i;
                if (result > int.MaxValue) return long.MaxValue;
            }

            return result;
        }

        private static void EnumerateCombinations(
            List<Point> cells, int size, int maxCombos, Func<int[], bool> visit)
        {
            if (size <= 0 || cells.Count < size) return;

            var pick = new int[size];
            int visited = 0;

            bool Walk(int depth, int start)
            {
                if (depth == size)
                {
                    visited++;
                    bool keepGoing = visit(pick);
                    return keepGoing && visited < maxCombos;
                }

                for (int i = start; i < cells.Count; i++)
                {
                    pick[depth] = i;
                    if (!Walk(depth + 1, i + 1)) return false;
                }

                return true;
            }

            Walk(0, 0);
        }

        // ---------------- P5：组合枚举热路径（扁平化等价加速，§8.3） ----------------
        //
        // 每组合原先分配 FindPath 的 Dictionary×2 + PriorityQueue、JaccardOverlap /
        // HalfSeparation 的 HashSet×4、ComputeKillzone 的 HashSet×1 与 LINQ 迭代器；
        // 现全部换成每图复用的扁平缓冲，稳态零分配。
        //
        // 等价性三条件（缺一不可，验收=全文件逐字节一致）：
        //   1. FastRoute 与 FindPath 弹出序逐位一致：优先级 (cost, cellId) 全局唯一
        //      （同格仅严格更优才入队，不同格 cellId 必异），cellId = y*Width+x 与
        //      (y,x) 字典序等价；同 Directions 松弛序 + 同懒删除/提前停规则；
        //   2. 简单路径无重复格 → HashSet 语义 == 掩膜计数；
        //   3. 指标公式与运算顺序逐字不变 → double 逐位一致 → best 与计数一致。
        private sealed class ComboContext
        {
            public readonly int Width;
            public readonly int Height;
            public readonly int Spawn;
            public readonly int Exit;
            public readonly bool[] Obstacles;
            public readonly int[] BaseIds;      // basePath 的 cellId 序列（start→goal）
            public readonly bool[] OnBase;
            public readonly bool[] Blocked;     // 当前组合掩膜
            public readonly bool[] OnNew;       // 当前新路掩膜
            public readonly int[] Dist;
            public readonly int[] Prev;
            public readonly int[] HeapCell;     // 堆键 (HeapCost, HeapCell) 为严格全序
            public readonly int[] HeapCost;
            public int HeapCount;
            public readonly int[] PathBuf;      // 新路 cellId 序列（start→goal）
            public int PathLen;
            public int Cost;

            public ComboContext(MapData map, List<Point> basePath)
            {
                Width = map.Width;
                Height = map.Height;
                int cells = Width * Height;
                Spawn = map.Spawn.Y * Width + map.Spawn.X;
                Exit = map.Exit.Y * Width + map.Exit.X;

                Obstacles = new bool[cells];
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    Obstacles[y * Width + x] = map.Obstacles[x, y];

                OnBase = new bool[cells];
                BaseIds = new int[basePath.Count];
                for (int i = 0; i < basePath.Count; i++)
                {
                    int id = basePath[i].Y * Width + basePath[i].X;
                    BaseIds[i] = id;
                    OnBase[id] = true;
                }

                Blocked = new bool[cells];
                OnNew = new bool[cells];
                Dist = new int[cells];
                Prev = new int[cells];
                HeapCell = new int[cells * 4 + 4];  // 每格仅在严格更优时入队，≤4 次每格
                HeapCost = new int[cells * 4 + 4];
                PathBuf = new int[cells];
            }
        }

        // 二叉最小堆，键 (cost, cell) 字典序。与 .NET PriorityQueue 在"键全局唯一"
        // 前提下弹出序完全一致（逐次弹出全局最小，序内容相同）。
        private static void HeapPush(ComboContext ctx, int cost, int cell)
        {
            int i = ctx.HeapCount++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                int pc = ctx.HeapCost[p], pcell = ctx.HeapCell[p];
                if (pc < cost || (pc == cost && pcell < cell)) break;
                ctx.HeapCost[i] = pc;
                ctx.HeapCell[i] = pcell;
                i = p;
            }
            ctx.HeapCost[i] = cost;
            ctx.HeapCell[i] = cell;
        }

        private static void HeapPop(ComboContext ctx)
        {
            int last = --ctx.HeapCount;
            int cost = ctx.HeapCost[last], cell = ctx.HeapCell[last];
            int i = 0;
            for (int half = ctx.HeapCount >> 1; i < half; )
            {
                int c = 2 * i + 1;
                int cc = ctx.HeapCost[c], ccell = ctx.HeapCell[c];
                int r = c + 1;
                if (r < ctx.HeapCount)
                {
                    int rc = ctx.HeapCost[r], rcell = ctx.HeapCell[r];
                    if (rc < cc || (rc == cc && rcell < ccell))
                    {
                        c = r; cc = rc; ccell = rcell;
                    }
                }
                if (cc < cost || (cc == cost && ccell < cell))
                {
                    ctx.HeapCost[i] = cc;
                    ctx.HeapCell[i] = ccell;
                    i = c;
                }
                else break;
            }
            ctx.HeapCost[i] = cost;
            ctx.HeapCell[i] = cell;
        }

        // 与 FindPath（软代价版）逐位同序的扁平化 Dijkstra。
        // 结果写入 ctx.PathBuf（start→goal cellId 序列）/ PathLen / Cost。
        private static void FastRoute(ComboContext ctx, int tollCost)
        {
            Array.Fill(ctx.Dist, int.MaxValue);
            ctx.HeapCount = 0;
            ctx.Dist[ctx.Spawn] = 0;
            ctx.Prev[ctx.Spawn] = -1;
            HeapPush(ctx, 0, ctx.Spawn);

            while (ctx.HeapCount > 0)
            {
                int cell = ctx.HeapCell[0];
                int cost = ctx.HeapCost[0];
                HeapPop(ctx);
                if (cost > ctx.Dist[cell]) continue;    // 懒删除，同 FindPath
                if (cell == ctx.Exit) break;            // 同 FindPath 提前停

                int x = cell % ctx.Width;
                int y = cell / ctx.Width;
                for (int d = 0; d < Directions.Length; d++)   // 与 FindPath 同松弛序
                {
                    int nx = x + Directions[d].Dx;
                    int ny = y + Directions[d].Dy;
                    if (nx < 0 || ny < 0 || nx >= ctx.Width || ny >= ctx.Height) continue;
                    int next = ny * ctx.Width + nx;
                    if (ctx.Obstacles[next]) continue;

                    int candidate = cost + (ctx.Blocked[next] ? tollCost : 1);
                    if (ctx.Dist[next] <= candidate) continue;

                    ctx.Dist[next] = candidate;
                    ctx.Prev[next] = cell;
                    HeapPush(ctx, candidate, next);
                }
            }

            if (ctx.Dist[ctx.Exit] == int.MaxValue)
            {
                ctx.PathLen = 0;
                ctx.Cost = int.MaxValue;
                return;
            }

            ctx.Cost = ctx.Dist[ctx.Exit];
            int len = 0;
            for (int cur = ctx.Exit; cur != -1; cur = ctx.Prev[cur])
                ctx.PathBuf[len++] = cur;

            for (int i = 0, j = len - 1; i < j; i++, j--)   // 反转为 start→goal
                (ctx.PathBuf[i], ctx.PathBuf[j]) = (ctx.PathBuf[j], ctx.PathBuf[i]);

            ctx.PathLen = len;
        }

        // ---------------- 单次封锁评估 ----------------

        private static SwitchReport EvaluateSwitches(
            MapData map,
            List<Point> basePath,
            int baseCost,
            double baseKillzone,
            int n,
            int maxCombos,
            int blockCost)
        {
            var report = new SwitchReport { N = n };

            var candidates = basePath
                .Where(p => !p.Equals(map.Spawn) && !p.Equals(map.Exit))
                .ToList();

            report.Candidates = candidates.Count;
            report.CombosExpected = CombinationCount(candidates.Count, n);

            if (candidates.Count < n) return report;

            var ctx = new ComboContext(map, basePath);
            int[] candidateIds = new int[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
                candidateIds[i] = candidates[i].Y * ctx.Width + candidates[i].X;

            // 基准质心每图固定：原实现在每组合内重复计算同一值（CentroidShift/
            // HalfSeparation 内部各调一次 Centroid(basePath)），现提升到循环外。
            double baseSumX = 0, baseSumY = 0;
            foreach (Point p in basePath)
            {
                baseSumX += p.X;
                baseSumY += p.Y;
            }
            Point baseCentroid = new Point(
                (int)Math.Round(baseSumX / basePath.Count),
                (int)Math.Round(baseSumY / basePath.Count));

            double bestScore = double.NegativeInfinity;
            SwitchSample? best = null;

            EnumerateCombinations(candidates, n, maxCombos, pick =>
            {
                for (int i = 0; i < pick.Length; i++)
                    ctx.Blocked[candidateIds[pick[i]]] = true;
                report.Combos++;

                FastRoute(ctx, blockCost);

                bool proceed = ctx.PathLen > 0;   // 软代价下不会为 false，保险起见

                bool tolled = false;
                bool routeChanged = false;
                double overlap = 0, halfSeparation = 0, shift = 0, delta = 0, killzoneGain = 0;
                int nextCost = 0;

                if (proceed)
                {
                    for (int i = 0; i < ctx.PathLen; i++)
                        ctx.OnNew[ctx.PathBuf[i]] = true;

                    nextCost = ctx.Cost;

                    // tolled：新路上是否有封锁格（原 nextPath.Any(blocked.Contains)）
                    for (int i = 0; i < ctx.PathLen; i++)
                        if (ctx.Blocked[ctx.PathBuf[i]]) { tolled = true; break; }

                    // routeChanged：原 !nextPath.SequenceEqual(basePath)
                    routeChanged = ctx.PathLen != ctx.BaseIds.Length;
                    if (!routeChanged)
                        for (int i = 0; i < ctx.PathLen; i++)
                            if (ctx.PathBuf[i] != ctx.BaseIds[i]) { routeChanged = true; break; }

                    if (tolled) report.Tolled++;
                    if (!tolled && routeChanged) report.Switched++;
                    if (nextCost == baseCost) report.EqualCost++;

                    // overlap：原 JaccardOverlap（简单路径无重复格，掩膜计数等价）
                    int inter = 0;
                    for (int i = 0; i < ctx.PathLen; i++)
                        if (ctx.OnBase[ctx.PathBuf[i]]) inter++;
                    int union = ctx.BaseIds.Length + ctx.PathLen - inter;
                    overlap = union == 0 ? 1.0 : (double)inter / union;

                    // 新路质心（整数逐步累加与原 double 累加在本量级下逐位一致）
                    int sumX = 0, sumY = 0;
                    for (int i = 0; i < ctx.PathLen; i++)
                    {
                        int id = ctx.PathBuf[i];
                        sumX += id % ctx.Width;
                        sumY += id / ctx.Width;
                    }
                    int cbx = (int)Math.Round((double)sumX / ctx.PathLen);
                    int cby = (int)Math.Round((double)sumY / ctx.PathLen);

                    // halfSeparation：原 HalfSeparation 的掩膜版（公式逐字不变）
                    double hdx = cbx - baseCentroid.X;
                    double hdy = cby - baseCentroid.Y;
                    if (Math.Abs(hdx) + Math.Abs(hdy) < 1e-9)
                    {
                        halfSeparation = 0;
                    }
                    else
                    {
                        double mx = (baseCentroid.X + cbx) / 2.0;
                        double my = (baseCentroid.Y + cby) / 2.0;

                        int countA = 0, ownA = 0;
                        for (int i = 0; i < ctx.BaseIds.Length; i++)
                        {
                            int id = ctx.BaseIds[i];
                            if (ctx.OnNew[id]) continue;
                            countA++;
                            if ((id % ctx.Width - mx) * hdx + (id / ctx.Width - my) * hdy <= 0)
                                ownA++;
                        }

                        if (countA == 0)
                        {
                            halfSeparation = 0;
                        }
                        else
                        {
                            int countB = 0, ownB = 0;
                            for (int i = 0; i < ctx.PathLen; i++)
                            {
                                int id = ctx.PathBuf[i];
                                if (ctx.OnBase[id]) continue;
                                countB++;
                                if ((id % ctx.Width - mx) * hdx + (id / ctx.Width - my) * hdy > 0)
                                    ownB++;
                            }
                            halfSeparation = countB == 0
                                ? 0
                                : Math.Min((double)ownA / countA, (double)ownB / countB);
                        }
                    }

                    // shift：原 CentroidShift（基准质心已提升到循环外，数值相同）
                    double sdx = cbx - baseCentroid.X;
                    double sdy = cby - baseCentroid.Y;
                    shift = Math.Sqrt(sdx * sdx + sdy * sdy);

                    delta = (double)(nextCost - baseCost) / Math.Max(1, baseCost);

                    // killzone：原 ComputeKillzone（OnNew 掩膜替代 pathSet）
                    int slots = 0;
                    for (int i = 0; i < ctx.PathLen; i++)
                    {
                        int id = ctx.PathBuf[i];
                        int px = id % ctx.Width;
                        int py = id / ctx.Width;
                        for (int dyy = -2; dyy <= 2; dyy++)
                        for (int dxx = -2; dxx <= 2; dxx++)
                        {
                            if (dxx == 0 && dyy == 0) continue;
                            int x = px + dxx;
                            int y = py + dyy;
                            if (x < 0 || y < 0 || x >= ctx.Width || y >= ctx.Height) continue;
                            int nid = y * ctx.Width + x;
                            if (ctx.Obstacles[nid]) continue;
                            if (ctx.OnNew[nid]) continue;
                            if (nid == ctx.Spawn || nid == ctx.Exit) continue;
                            slots++;
                        }
                    }
                    double killzone = (double)slots / ctx.PathLen;
                    killzoneGain = (killzone - baseKillzone) / Math.Max(0.001, baseKillzone);

                    bool switched = !tolled && routeChanged;
                    if (PassesOtherHalf(switched, overlap, halfSeparation, delta)) report.Passing++;
                    if (PassesAdaptive(switched, overlap, halfSeparation, delta, n))
                        report.PassingAdaptive++;
                }

                if (proceed)
                {
                    bool switched = !tolled && routeChanged;
                    double score = ScoreSwitch(switched, overlap, halfSeparation, delta, killzoneGain);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        var blockers = new List<Point>(pick.Length);
                        for (int i = 0; i < pick.Length; i++)
                            blockers.Add(candidates[pick[i]]);
                        var nextPath = new List<Point>(ctx.PathLen);
                        for (int i = 0; i < ctx.PathLen; i++)
                        {
                            int id = ctx.PathBuf[i];
                            nextPath.Add(new Point(id % ctx.Width, id / ctx.Width));
                        }
                        best = new SwitchSample
                        {
                            Moves = ctx.PathLen - 1,
                            Cost = nextCost,
                            Tolled = tolled,
                            Overlap = overlap,
                            HalfSeparation = halfSeparation,
                            CentroidShift = shift,
                            Delta = delta,
                            KillzoneGain = killzoneGain,
                            Score = score,
                            Blockers = blockers,
                            NewPath = nextPath
                        };
                    }

                    for (int i = 0; i < ctx.PathLen; i++)
                        ctx.OnNew[ctx.PathBuf[i]] = false;
                }

                for (int i = 0; i < pick.Length; i++)
                    ctx.Blocked[candidateIds[pick[i]]] = false;

                return true;
            });

            report.CapHit = report.CombosExpected > maxCombos;
            report.Best = best;
            return report;
        }

        private static MapAnalysis AnalyzeMap(MapData map, int maxN, int maxCombos, int blockCost)
        {
            var analysis = new MapAnalysis
            {
                BlockCost = blockCost
            };

            List<Point> basePath = FindPath(
                map.Spawn, map.Exit, map.Obstacles, map.Width, map.Height,
                null, 0, out int baseCost);

            analysis.HasPath = basePath.Count > 0;
            if (!analysis.HasPath) return analysis;

            analysis.PathMoves = basePath.Count - 1;
            analysis.BaseCost = baseCost;
            analysis.PrintedMatchesCanonical = map.Path.Count == basePath.Count
                                               && map.Path.SequenceEqual(basePath);

            (int flow, List<Point> cut) = ComputeMinVertexCut(
                map.Obstacles, map.Width, map.Height, map.Spawn, map.Exit);
            analysis.MinCut = flow;
            analysis.Choke = cut;

            analysis.Killzone = ComputeKillzone(basePath, map);

            // 替代走廊探测：把主路内部格设为极高代价（近似硬墙）后取得的最优路。
            // 探针同样会抬高低价共享格（θ 的茎段），因此要把这些格的溢价扣回去。
            var inner = new HashSet<Point>(basePath.Where(p => !p.Equals(map.Spawn) && !p.Equals(map.Exit)));
            List<Point> alternative = FindPath(
                map.Spawn, map.Exit, map.Obstacles, map.Width, map.Height,
                inner, ProbeBlockCost, out int alternativeCost);

            int sharedProbeCells = alternative.Count(cell => inner.Contains(cell));
            int adjustedAlternativeCost = alternativeCost - sharedProbeCells * (ProbeBlockCost - 1);

            bool hasAlternative = alternative.Count > 0 && !alternative.SequenceEqual(basePath);
            analysis.AlternativeCost = hasAlternative ? adjustedAlternativeCost : -1;
            analysis.SwitchPrediction = hasAlternative
                ? Math.Max(0, (int)Math.Ceiling(
                    (double)(adjustedAlternativeCost - baseCost) / Math.Max(1, blockCost - 1)))
                : -1;

            long comboMark = GenerationProfiler.Mark();
            for (int n = 1; n <= maxN; n++)
            {
                analysis.Reports.Add(EvaluateSwitches(
                    map, basePath, baseCost, analysis.Killzone, n, maxCombos, blockCost));
            }
            GenerationProfiler.AddStage("combo_enum", GenerationProfiler.Since(comboMark));

            return analysis;
        }

        // ---------------- 输出 ----------------

        private static void PrintAnalysis(MapData map, MapAnalysis analysis)
        {
            Console.WriteLine();
            Console.WriteLine($"=== P1 strategic analysis (soft-cost model, W = {analysis.BlockCost}) ===");

            if (!analysis.HasPath)
            {
                Console.WriteLine("No route between gates.");
                return;
            }

            Console.WriteLine($"Cheapest path: {analysis.PathMoves} moves / cost {analysis.BaseCost}"
                              + $" (printed path matches: {(analysis.PrintedMatchesCanonical ? "yes" : "no")})");
            Console.WriteLine($"Natural min-cut (diagnostic): {analysis.MinCut}"
                              + (analysis.Choke.Count > 0
                                  ? "; choke cells = " + string.Join(", ", analysis.Choke)
                                  : "; no finite choke"));
            Console.WriteLine(analysis.SwitchPrediction >= 0
                ? $"Disjoint alternative corridor: cost {analysis.AlternativeCost}"
                  + $" -> predicted n_switch = {analysis.SwitchPrediction}"
                : "Disjoint alternative corridor: none (all alternatives share the main route)");
            Console.WriteLine($"Killzone exposure: {analysis.Killzone:F2} tower slots per path cell");

            foreach (SwitchReport report in analysis.Reports)
            {
                if (report.Best == null)
                {
                    Console.WriteLine($"Blocking n={report.N}: no candidate combos.");
                    continue;
                }

                SwitchSample best = report.Best.Value;
                Console.WriteLine(
                    $"Blocking n={report.N}: candidates {report.Candidates}, "
                    + $"combos {report.Combos}/{report.CombosExpected}"
                    + (report.CapHit ? " (capped)" : string.Empty)
                    + $", tolled {report.Tolled}, switched {report.Switched}"
                    + $", equal-cost {report.EqualCost}, passing {report.Passing}"
                    + $", passing-adaptive {report.PassingAdaptive}");
                Console.WriteLine(
                    $"  best: overlap {best.Overlap:F2}, half-sep {best.HalfSeparation:F2}, "
                    + $"shift {best.CentroidShift:F1}, delta {best.Delta:+0.0%;-0.0%}, "
                    + $"killzone {best.KillzoneGain:+0.0%;-0.0%} -> other-half "
                    + (PassesOtherHalf(!best.Tolled, best.Overlap, best.HalfSeparation, best.Delta)
                        ? "PASS" : "FAIL")
                    + $" / adaptive "
                    + (PassesAdaptive(!best.Tolled, best.Overlap, best.HalfSeparation,
                                      best.Delta, report.N) ? "PASS" : "FAIL")
                    + $" / half-sep {HalfSeparationTier(best.HalfSeparation)}");
                Console.WriteLine(
                    $"  blockers: {string.Join(", ", best.Blockers)}; new path {best.Moves} moves / cost {best.Cost}");
            }
        }

        // 指标定义版本：口径变更时递增，汇总层据此识别数据来源。
        // p1-v4：新增地图级三态列（map_pass_*）。旧列语义不变；
        // passes_other_half / passes_adaptive 仍为 best-sample 展示口径，
        // 地图级合格性改由 passing_combinations 是否 > 0 决定（P0）。
        // P3 (p3-v5) = p1-v4 + 表尾 4 个 best 样本分离度诊断列；判据零变化。
        // P8 (p8-v6) = p3-v5 + 表尾 5 个难度诊断列（只读，不参与任何判据）。
        // P9 (p9-v7) = p8-v6 + 表尾 1 列 theta_flank（θ 行 = 实际封边比例，v3 行留空）。
        private const string AnalysisMetricVersion = "p9-v7";

        private static readonly string[] CsvHeader =
        {
            "seed", "status", "width", "height", "obstacles", "printed_moves", "canonical_moves",
            "printed_matches", "turns", "mincut_natural", "choke_count", "w", "n", "combos",
            "tolled", "switched", "equal_cost", "best_overlap", "best_half_sep", "best_shift",
            "best_delta", "best_killzone_gain", "passes_other_half", "cert_blockers", "cert_moves",
            "n_switch_pred", "combos_expected", "cap_hit", "passing_combinations", "metric_version",
            "passes_adaptive", "passing_adaptive_combinations", "half_sep_tier", "adaptive_delta_max",
            "map_pass_other_half", "map_pass_adaptive", "map_pass_status",
            "best_sep_min", "best_sep_adj2", "best_sep_med", "best_sep_run",
            "diff_d1", "diff_d2_open", "diff_d3_ucount", "diff_d3_usize", "diff_score",
            "theta_flank"
        };

        // 统一补齐列数，避免不同状态行错位。
        private static void AddRow(List<string> rows, List<string> fields)
        {
            while (fields.Count < CsvHeader.Length) fields.Add(string.Empty);
            rows.Add(string.Join(",", fields));
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

        private static void RunAnalyzerBatch(string[] args)
        {
            int count = 200;
            int maxN = Profile.AnalysisN;
            int maxCombos = Profile.ComboCap;
            int baseSeed = Settings.RandomSeed;
            int blockCost = Profile.BlockCost;   // P6：v3 的 W（--w 可改）；θ 每图用拟合值
            int jobs = 1;                        // P6：并行度，1 = 顺序驱动（缺省，行为不变）
            string csvPath = Path.Combine("analysis", "baseline_v1_1_soft.csv");
            // P11：初值取 Settings 预设（脚本内直接调难度），命令行 --difficulty 覆盖。
            double? difficultyLo = Settings.DifficultyLo;   // P9/P10：难度选入区间
            double? difficultyHi = Settings.DifficultyHi;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--count":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCount) && parsedCount > 0)
                        {
                            count = parsedCount;
                            i++;
                        }
                        break;
                    case "--nmax":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedN)
                            && parsedN >= 1 && parsedN <= 4)
                        {
                            maxN = parsedN;
                            i++;
                        }
                        break;
                    case "--combos":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCombos)
                            && parsedCombos > 0)
                        {
                            maxCombos = parsedCombos;
                            i++;
                        }
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed))
                        {
                            baseSeed = parsedSeed;
                            i++;
                        }
                        break;
                    case "--w":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedW) && parsedW >= 2)
                        {
                            blockCost = parsedW;
                            i++;
                        }
                        break;
                    case "--jobs":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedJobs) && parsedJobs >= 1)
                        {
                            jobs = parsedJobs;
                            i++;
                        }
                        break;
                    case "--csv":
                        if (i + 1 < args.Length)
                        {
                            csvPath = args[i + 1];
                            i++;
                        }
                        break;
                    case "--engine":
                        if (i + 1 < args.Length)
                        {
                            if (args[i + 1].Equals("theta", StringComparison.OrdinalIgnoreCase))
                                Settings.UseTheta = true;
                            else if (args[i + 1].Equals("v3", StringComparison.OrdinalIgnoreCase))
                                Settings.UseTheta = false;
                            else
                            {
                                Console.WriteLine($"--engine needs v3 or theta, got '{args[i + 1]}'");
                                Environment.ExitCode = 1;
                                return;
                            }
                            i++;
                        }
                        break;
                    case "--difficulty":
                        if (i + 1 < args.Length)
                        {
                            // 值写成 lo,hi（或 lo:hi），与 delta-window 同款宽容。
                            string[] parts = args[i + 1].Split(':', ',');
                            if (parts.Length == 2
                                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffLo)
                                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffHi)
                                && diffLo >= 0 && diffHi <= 1 && diffLo <= diffHi)
                            {
                                difficultyLo = diffLo;
                                difficultyHi = diffHi;
                            }
                            else
                            {
                                Console.WriteLine($"--difficulty needs 'lo,hi' with 0 <= lo <= hi <= 1, got '{args[i + 1]}'");
                                Environment.ExitCode = 1;
                                return;
                            }
                            i++;
                        }
                        break;
                    case "--profile":
                        GenerationProfiler.Enabled = true;
                        break;
                }
            }

            // P10：v3 引擎的 --difficulty 走拒绝式选入（出区间的 seed 直接跳过）；
            // θ 保持 P9 调制语义（封边比例二分）。缺省（无 --difficulty）路径零变化。
            bool v3Selection = difficultyLo.HasValue && !Settings.UseTheta;
            int budget = v3Selection ? count * 10 : count;   // P10：选入扫描预算（seed 数上限）

            // θ 约束：方形且 size ≥ θ 最小尺寸。入口拦截，避免整批重复失败。
            if (Settings.UseTheta
                && (Settings.MapWidth != Settings.MapHeight
                    || Settings.MapWidth < Profile.ThetaMinSize))
            {
                Console.WriteLine(
                    $"theta engine requires a square map with size >= {Profile.ThetaMinSize} "
                    + $"(got {Settings.MapWidth}x{Settings.MapHeight}); adjust --from/--to or use the v3 engine.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine("=== P1 analyzer (soft-cost blocking) ===");
            Console.WriteLine($"engine: {(Settings.UseTheta ? "theta skeleton (W fitted per map)" : "v3 rejection sampling")}"
                              + (GenerationProfiler.Enabled ? " | profile: on" : "")
                              + (jobs > 1 ? $" | jobs: {jobs}" : ""));
            Console.WriteLine($"maps: {count} | n = 1..{maxN} | W = {blockCost} | "
                              + $"combo cap: {maxCombos}/n | base seed: {baseSeed}");
            if (difficultyLo.HasValue)
            {
                Console.WriteLine(Settings.UseTheta
                    ? $"difficulty target: [{difficultyLo.Value:F2}, {difficultyHi!.Value:F2}] "
                      + "(per-map flank-ratio bisection; misses fall back to default sealing)"
                    : $"difficulty target: [{difficultyLo.Value:F2}, {difficultyHi!.Value:F2}] "
                      + "(v3 rejection selection: seeds outside the range are skipped; budget = count*10)");
            }
            Console.WriteLine($"csv: {Path.GetFullPath(csvPath)}");
            Console.WriteLine();

            var rows = new List<string> { string.Join(",", CsvHeader) };

            var kCounts = new SortedDictionary<int, int>();
            var passCounts = new int[maxN + 1];
            var comboCnt = new int[maxN + 1];
            var tolledCnt = new int[maxN + 1];
            var switchedCnt = new int[maxN + 1];
            var equalCostCnt = new int[maxN + 1];
            var mapPassCounts = new int[maxN + 1];
            var mapPassAdaptiveCounts = new int[maxN + 1];
            var unknownTruncatedCounts = new int[maxN + 1];
            var failCompleteCounts = new int[maxN + 1];
            var overlapSum = new double[maxN + 1];
            var halfSepSum = new double[maxN + 1];
            var deltaSum = new double[maxN + 1];
            var shiftSum = new double[maxN + 1];

            int generated = 0;
            int analysable = 0;
            int generationFailures = 0;
            int difficultyMisses = 0;   // P9：难度区间不可达图数（回退缺省密度）
            int scanned = 0;            // P10：v3 选入实际扫描的 seed 数（含未命中）
            long attemptSum = 0;
            int attemptMax = 0;
            int printedMatches = 0;
            var predictions = new List<int>();

            // ---------------- P6：按图并行化 ----------------
            // 每图种子独立（seed = baseSeed + i*7919）、生成器与分析器均为纯函数，
            // 图间零共享状态；W 也已参数化（v3 = --w，θ = 每图拟合值）。
            // ProcessMap 只读入参、只写自己的槽位；所有共享累加器由主线程
            // 在合并阶段按图序更新 —— 任意 --jobs 下 CSV 与汇总逐字节一致。

            BatchSlot ProcessMap(int i)
            {
                int seed = unchecked(baseSeed + i * 7919);
                var slot = new BatchSlot(maxN);
                MapData? map;
                double? thetaFlank = null;   // P9：θ 行写入实际封边比例，v3 行留空
                if (Settings.UseTheta)
                {
                    // P2：生成阶段分派（与主流程一致，分析层共用）。θ 不消耗 Random。
                    long genMark = GenerationProfiler.Mark();
                    ThetaResult theta = BuildThetaMainMap(seed, difficultyLo, difficultyHi);
                    map = theta.Map;
                    slot.BlockCost = theta.BlockCost;
                    if (theta.Map != null) thetaFlank = theta.DecorateFlank;
                    if (theta.Map != null && !theta.DifficultyInRange) slot.DifficultyMiss = true;
                    GenerationProfiler.RecordGenerate(genMark, -1);
                    if (map == null)
                        GenerationProfiler.ThetaReject(
                            string.IsNullOrWhiteSpace(theta.Reason) ? "unknown" : theta.Reason);
                }
                else
                {
                    slot.BlockCost = blockCost;
                    var random = new Random(seed);
                    map = GenerateMap(random, seed);
                    if (difficultyLo.HasValue)
                    {
                        // P10：v3 拒绝式选入——生成失败或 diff_score 出区间的 seed 直接跳过
                        // （无输出行、无分析），由主循环继续扫描下一个 seed。
                        double selectionScore = map == null ? -1.0 : ComputeDifficulty(map).Score;
                        if (map == null
                            || selectionScore < difficultyLo.Value
                            || selectionScore > difficultyHi!.Value)
                        {
                            slot.SelectionSkip = true;
                            return slot;
                        }
                    }
                }

                if (map == null)
                {
                    slot.GenerationFailure = true;
                    AddRow(slot.Rows, new List<string>
                    {
                        Num(seed), "generation_failed",
                        Num(Settings.MapWidth), Num(Settings.MapHeight)
                    });
                    return slot;
                }

                slot.Generated = true;
                slot.Attempts = map.AttemptsUsed;
                long analyzeMark = GenerationProfiler.Mark();
                MapAnalysis analysis = AnalyzeMap(map, maxN, maxCombos, slot.BlockCost);
                GenerationProfiler.RecordAnalyze(analyzeMark);

                if (!analysis.HasPath)
                {
                    AddRow(slot.Rows, new List<string>
                    {
                        Num(seed), "unreachable", Num(map.Width), Num(map.Height),
                        Num(map.ObstacleCount), Num(map.Path.Count - 1)
                    });
                    return slot;
                }

                slot.Analysable = true;
                slot.PrintedMatch = analysis.PrintedMatchesCanonical ? 1 : 0;
                slot.MinCut = analysis.MinCut;
                slot.SwitchPrediction = analysis.SwitchPrediction;

                foreach (SwitchReport report in analysis.Reports)
                {
                    int n = report.N;
                    slot.ComboCnt[n] += report.Combos;
                    slot.TolledCnt[n] += report.Tolled;
                    slot.SwitchedCnt[n] += report.Switched;
                    slot.EqualCostCnt[n] += report.EqualCost;

                    // P0：地图级三态。合格性看「是否存在合格组合」，与最佳样本解耦。
                    bool mapPass = report.Passing > 0;
                    bool mapPassAdaptive = report.PassingAdaptive > 0;
                    string passStatus = MapPassStatus(report.Passing, report.CapHit);
                    if (mapPass) slot.MapPassCounts[n]++;
                    if (mapPassAdaptive) slot.MapPassAdaptiveCounts[n]++;
                    if (passStatus == "unknown_truncated") slot.UnknownTruncatedCounts[n]++;
                    else if (passStatus == "fail_complete") slot.FailCompleteCounts[n]++;

                    var row = new List<string>
                    {
                        Num(seed), "ok", Num(map.Width), Num(map.Height), Num(map.ObstacleCount),
                        Num(map.Path.Count - 1), Num(analysis.PathMoves),
                        analysis.PrintedMatchesCanonical ? "1" : "0",
                        Num(map.Metrics.Turns), Num(analysis.MinCut), Num(analysis.Choke.Count),
                        Num(slot.BlockCost), Num(n), Num(report.Combos),
                        Num(report.Tolled), Num(report.Switched), Num(report.EqualCost)
                    };

                    if (report.Best == null)
                    {
                        for (int pad = 0; pad < 5; pad++) row.Add(string.Empty);
                        row.Add("0");
                        row.Add(string.Empty);
                        row.Add(string.Empty);
                        row.Add(Num(analysis.SwitchPrediction));
                        row.Add(Num(report.CombosExpected));
                        row.Add(report.CapHit ? "1" : "0");
                        row.Add(Num(report.Passing));
                        row.Add(AnalysisMetricVersion);
                        row.Add("0");
                        row.Add(Num(report.PassingAdaptive));
                        row.Add(string.Empty);
                        row.Add(Num(AdaptiveDeltaMax(n), "F3"));
                        row.Add(mapPass ? "1" : "0");
                        row.Add(mapPassAdaptive ? "1" : "0");
                        row.Add(passStatus);
                        // P9：θ 封边比例列（v3 行由 AddRow 补空）。
                        if (thetaFlank.HasValue) row.Add(Num(thetaFlank.Value, "F3"));
                        AddRow(slot.Rows, row);
                        continue;
                    }

                    SwitchSample best = report.Best.Value;
                    bool passes = PassesOtherHalf(
                        !best.Tolled, best.Overlap, best.HalfSeparation, best.Delta);

                    if (passes) slot.PassCounts[n]++;
                    slot.OverlapSum[n] += best.Overlap;
                    slot.HalfSepSum[n] += best.HalfSeparation;
                    slot.DeltaSum[n] += best.Delta;
                    slot.ShiftSum[n] += best.CentroidShift;

                    row.Add(Num(best.Overlap, "F3"));
                    row.Add(Num(best.HalfSeparation, "F3"));
                    row.Add(Num(best.CentroidShift, "F2"));
                    row.Add(Num(best.Delta, "F3"));
                    row.Add(Num(best.KillzoneGain, "F3"));
                    row.Add(passes ? "1" : "0");
                    row.Add("\"" + string.Join("|", best.Blockers) + "\"");
                    row.Add(Num(best.Moves));
                    row.Add(Num(analysis.SwitchPrediction));
                    row.Add(Num(report.CombosExpected));
                    row.Add(report.CapHit ? "1" : "0");
                    row.Add(Num(report.Passing));
                    row.Add(AnalysisMetricVersion);
                    row.Add(PassesAdaptive(!best.Tolled, best.Overlap, best.HalfSeparation,
                                         best.Delta, n) ? "1" : "0");
                    row.Add(Num(report.PassingAdaptive));
                    row.Add(HalfSeparationTier(best.HalfSeparation));
                    row.Add(Num(AdaptiveDeltaMax(n), "F3"));
                    row.Add(mapPass ? "1" : "0");
                    row.Add(mapPassAdaptive ? "1" : "0");
                    row.Add(passStatus);

                    // P3：best 样本分离度诊断列（表尾追加；Best==null 分支由 AddRow 补空）。
                    (int sepMin, double sepAdj2, int sepMed, int sepRun) = ComputeSeparation(
                        map.Path, best.NewPath, map.Obstacles, map.Width, map.Height);
                    row.Add(Num(sepMin));
                    row.Add(Num(sepAdj2, "F3"));
                    row.Add(Num(sepMed));
                    row.Add(Num(sepRun));

                    // P8：难度诊断列（图属性，只读；Best==null 分支由 AddRow 补空）。
                    (double diffD1, double diffD2, int diffUCount, int diffUSize, double diffScore) =
                        ComputeDifficulty(map);
                    row.Add(Num(diffD1, "F3"));
                    row.Add(Num(diffD2, "F3"));
                    row.Add(Num(diffUCount));
                    row.Add(Num(diffUSize));
                    row.Add(Num(diffScore, "F3"));

                    // P9：θ 封边比例列（v3 行由 AddRow 补空）。
                    if (thetaFlank.HasValue) row.Add(Num(thetaFlank.Value, "F3"));
                    AddRow(slot.Rows, row);
                }

                return slot;
            }

            // 合并只在主线程按图序执行：行追加与累加顺序和顺序模式逐位一致。
            void MergeSlot(BatchSlot slot)
            {
                rows.AddRange(slot.Rows);
                if (slot.GenerationFailure) generationFailures++;
                if (slot.DifficultyMiss) difficultyMisses++;
                if (!slot.Generated) return;

                generated++;
                attemptSum += slot.Attempts;
                attemptMax = Math.Max(attemptMax, slot.Attempts);
                if (!slot.Analysable) return;

                analysable++;
                printedMatches += slot.PrintedMatch;
                if (slot.SwitchPrediction >= 0) predictions.Add(slot.SwitchPrediction);

                kCounts.TryGetValue(slot.MinCut, out int kSeen);
                kCounts[slot.MinCut] = kSeen + 1;

                for (int n = 1; n <= maxN; n++)
                {
                    comboCnt[n] += slot.ComboCnt[n];
                    tolledCnt[n] += slot.TolledCnt[n];
                    switchedCnt[n] += slot.SwitchedCnt[n];
                    equalCostCnt[n] += slot.EqualCostCnt[n];
                    mapPassCounts[n] += slot.MapPassCounts[n];
                    mapPassAdaptiveCounts[n] += slot.MapPassAdaptiveCounts[n];
                    unknownTruncatedCounts[n] += slot.UnknownTruncatedCounts[n];
                    failCompleteCounts[n] += slot.FailCompleteCounts[n];
                    passCounts[n] += slot.PassCounts[n];
                    overlapSum[n] += slot.OverlapSum[n];
                    halfSepSum[n] += slot.HalfSepSum[n];
                    deltaSum[n] += slot.DeltaSum[n];
                    shiftSum[n] += slot.ShiftSum[n];
                }
            }

            void PrintProgress(int processed)
            {
                if (processed % 25 == 0)
                    Console.WriteLine(
                        $"  processed {processed}/{count} (generated {generated}, failures {generationFailures})");
            }

            GenerationProfiler.ParallelJobs = jobs;
            if (jobs == 1)
            {
                // 缺省路径：顺序驱动，与 P5 及之前的输出逐位一致。
                // P10：v3 选入模式下沿预算扫描 seed，凑满 count 即早停（SelectionSkip 不计入）。
                for (int i = 0; i < budget; i++)
                {
                    scanned++;
                    BatchSlot slot = ProcessMap(i);
                    if (slot.SelectionSkip) { PrintProgress(scanned); continue; }
                    MergeSlot(slot);
                    PrintProgress(scanned);
                    if (v3Selection && generated >= count) break;
                }
            }
            else
            {
                // 并行路径：工作线程只填自己的槽位，主线程按图序合并。
                // MaxDegreeOfParallelism 精确兑现 --jobs 语义（上限 = N，不随机器核数放大）。
                // P10：选入模式预算固定 ⇒ 槽位集合固定，主线程按序只合并"前 count 个命中"，
                // 与顺序早停取到完全相同的 slot 序列 ⇒ 任意 --jobs 逐字节一致。
                var slots = new BatchSlot[budget];
                Parallel.For(0, budget, new ParallelOptions { MaxDegreeOfParallelism = jobs },
                    i => slots[i] = ProcessMap(i));
                for (int i = 0; i < budget && (!v3Selection || generated < count); i++)
                {
                    scanned++;
                    if (slots[i].SelectionSkip) { PrintProgress(scanned); continue; }
                    MergeSlot(slots[i]);
                    PrintProgress(scanned);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));

            if (GenerationProfiler.Enabled)
            {
                string profilePath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".", "profile_report.txt");
                File.WriteAllText(profilePath, GenerationProfiler.Report());
                Console.WriteLine($"profile: {Path.GetFullPath(profilePath)}");
            }

            Console.WriteLine();
            Console.WriteLine("=== summary ===");
            Console.WriteLine($"maps requested {count} | generated {generated} | generation failures {generationFailures}");
            if (difficultyLo.HasValue)
            {
                if (Settings.UseTheta)
                    Console.WriteLine($"difficulty target [{difficultyLo.Value:F2}, {difficultyHi!.Value:F2}]: "
                                      + $"{difficultyMisses}/{generated} maps unreachable -> default flank ratio "
                                      + "(see theta_flank column)");
                else
                {
                    // P10：v3 拒绝式选入汇总。
                    Console.WriteLine($"difficulty selection [{difficultyLo.Value:F2}, {difficultyHi!.Value:F2}]: "
                                      + $"accepted {generated}/{count} | seeds scanned {scanned}/{budget}");
                    if (generated < count)
                        Console.WriteLine($"warning: only {generated}/{count} maps within range after "
                                          + $"{budget}-seed budget; widen the range or raise --count.");
                }
            }
            Console.WriteLine($"rejection attempts: avg {(double)attemptSum / Math.Max(1, generated):F1}"
                              + $" | max {attemptMax} | cap {Settings.MaxGenerationAttempts}");
            Console.WriteLine("natural min-cut distribution: "
                              + string.Join(", ", kCounts.Select(pair => $"k={pair.Key}:{pair.Value}")));
            Console.WriteLine($"printed path equals canonical path: {printedMatches}/{analysable}"
                              + $" ({(double)printedMatches / Math.Max(1, analysable):P1})");

            if (predictions.Count > 0)
            {
                Console.WriteLine("n_switch prediction: "
                                  + string.Join(", ", predictions
                                      .GroupBy(value => value)
                                      .OrderBy(group => group.Key)
                                      .Select(group => $"n={group.Key}:{group.Count()}")));
            }
            else
            {
                Console.WriteLine("n_switch prediction: none (no disjoint alternative corridor)");
            }

            for (int n = 1; n <= maxN; n++)
            {
                if (comboCnt[n] == 0) continue;

                double passRate = (double)passCounts[n] / Math.Max(1, analysable);
                double tolledRate = (double)tolledCnt[n] / comboCnt[n];
                double switchedRate = (double)switchedCnt[n] / comboCnt[n];
                double equalRate = (double)equalCostCnt[n] / comboCnt[n];
                double avgOverlap = overlapSum[n] / Math.Max(1, analysable);
                double avgHalfSep = halfSepSum[n] / Math.Max(1, analysable);
                double avgDelta = deltaSum[n] / Math.Max(1, analysable);
                double avgShift = shiftSum[n] / Math.Max(1, analysable);

                Console.WriteLine(
                    $"n={n}: other-half pass (best-based legacy) {passCounts[n]}/{analysable} ({passRate:P1}) | "
                    + $"avg overlap {avgOverlap:F2} | avg half-sep {avgHalfSep:F2} | "
                    + $"avg delta {avgDelta:+0.0%;-0.0%} | avg shift {avgShift:F1} cells | "
                    + $"tolled {tolledRate:P1} | switched {switchedRate:P1} | equal-cost {equalRate:P1}");
                Console.WriteLine(
                    $"n={n}: map-level pass {mapPassCounts[n]}/{analysable} "
                    + $"({(double)mapPassCounts[n] / Math.Max(1, analysable):P1})"
                    + $" | adaptive {mapPassAdaptiveCounts[n]}"
                    + $" | unknown_truncated {unknownTruncatedCounts[n]}"
                    + $" | fail_complete {failCompleteCounts[n]}");
            }

            Console.WriteLine();
            Console.WriteLine("legend: switched = route fully avoids blocked cells; "
                              + "tolled = route still steps on blocked cells and pays W; "
                              + "other-half pass = switched AND overlap<=0.35 AND half-sep>=0.70 AND delta in [8%,35%]");
            Console.WriteLine("legend: map-level pass = at least one blocking combination meets the formal"
                              + " criteria (passing_combinations > 0); unknown_truncated = cap hit with no"
                              + " passing combination found (inconclusive, never counted as failure);"
                              + " fail_complete = complete enumeration with no passing combination.");
        }

        // P6：单图结果槽位 —— ProcessMap 填充、主线程按图序合并。
        // 每图独享一个实例，槽位之间零共享，并行填充即无竞态。
        private sealed class BatchSlot
        {
            public readonly List<string> Rows = new List<string>();
            public bool Generated;
            public bool GenerationFailure;
            public bool DifficultyMiss;   // P9：难度区间不可达（回退缺省密度）
            public bool SelectionSkip;    // P10：v3 选入未命中（diff_score 出区间或生成失败，无输出行）
            public bool Analysable;
            public int BlockCost;
            public int Attempts;
            public int PrintedMatch;
            public int MinCut;
            public int SwitchPrediction = -1;
            public readonly int[] ComboCnt;
            public readonly int[] TolledCnt;
            public readonly int[] SwitchedCnt;
            public readonly int[] EqualCostCnt;
            public readonly int[] MapPassCounts;
            public readonly int[] MapPassAdaptiveCounts;
            public readonly int[] UnknownTruncatedCounts;
            public readonly int[] FailCompleteCounts;
            public readonly int[] PassCounts;
            public readonly double[] OverlapSum;
            public readonly double[] HalfSepSum;
            public readonly double[] DeltaSum;
            public readonly double[] ShiftSum;

            public BatchSlot(int maxN)
            {
                ComboCnt = new int[maxN + 1];
                TolledCnt = new int[maxN + 1];
                SwitchedCnt = new int[maxN + 1];
                EqualCostCnt = new int[maxN + 1];
                MapPassCounts = new int[maxN + 1];
                MapPassAdaptiveCounts = new int[maxN + 1];
                UnknownTruncatedCounts = new int[maxN + 1];
                FailCompleteCounts = new int[maxN + 1];
                PassCounts = new int[maxN + 1];
                OverlapSum = new double[maxN + 1];
                HalfSepSum = new double[maxN + 1];
                DeltaSum = new double[maxN + 1];
                ShiftSum = new double[maxN + 1];
            }
        }

        // 尺寸扫描：11x11 → 16x16，每个尺寸各自批量分析。
        private static void RunSizeSweep(string[] args)
        {
            int from = 11;
            int to = 16;
            int count = 50;
            int maxN = Profile.AnalysisN;
            int maxCombos = Profile.ComboCap;
            int baseSeed = Settings.RandomSeed;
            string directory = Path.Combine("analysis", "sweep");
            bool engineTheta = false;
            bool profile = false;
            int blockCost = Profile.BlockCost;   // P6：v3 的 W（--w 可改），随参数转发给各批次
            int jobs = 1;                        // P6：并行度，转发给 RunAnalyzerBatch
            // P11 修复：--difficulty 现在被解析并转发；缺省取 Settings 预设（批处理同源）。
            double? difficultyLo = Settings.DifficultyLo;
            double? difficultyHi = Settings.DifficultyHi;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--from":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedFrom) && parsedFrom >= 4)
                        {
                            from = parsedFrom;
                            i++;
                        }
                        break;
                    case "--to":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedTo) && parsedTo >= 4)
                        {
                            to = parsedTo;
                            i++;
                        }
                        break;
                    case "--count":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCount) && parsedCount > 0)
                        {
                            count = parsedCount;
                            i++;
                        }
                        break;
                    case "--nmax":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedN)
                            && parsedN >= 1 && parsedN <= 4)
                        {
                            maxN = parsedN;
                            i++;
                        }
                        break;
                    case "--combos":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCombos)
                            && parsedCombos > 0)
                        {
                            maxCombos = parsedCombos;
                            i++;
                        }
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed))
                        {
                            baseSeed = parsedSeed;
                            i++;
                        }
                        break;
                    case "--w":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedW) && parsedW >= 2)
                        {
                            blockCost = parsedW;
                            i++;
                        }
                        break;
                    case "--jobs":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedJobs) && parsedJobs >= 1)
                        {
                            jobs = parsedJobs;
                            i++;
                        }
                        break;
                    case "--out":
                        if (i + 1 < args.Length)
                        {
                            directory = args[i + 1];
                            i++;
                        }
                        break;
                    case "--engine":
                        if (i + 1 < args.Length)
                        {
                            if (args[i + 1].Equals("theta", StringComparison.OrdinalIgnoreCase))
                                engineTheta = true;
                            else if (args[i + 1].Equals("v3", StringComparison.OrdinalIgnoreCase))
                                engineTheta = false;
                            else
                            {
                                Console.WriteLine($"--engine needs v3 or theta, got '{args[i + 1]}'");
                                Environment.ExitCode = 1;
                                return;
                            }
                            i++;
                        }
                        break;
                    case "--profile":
                        profile = true;
                        break;
                    case "--difficulty":
                        // P11 修复：sweep 原先把该参数吞掉（switch 无此分支），导致难度目标失效。
                        // 语义与 analyze 完全一致：θ 二分封边比例，v3 拒绝式选入。
                        if (i + 1 < args.Length)
                        {
                            string[] parts = args[i + 1].Split(':', ',');
                            if (parts.Length == 2
                                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffLo)
                                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double diffHi)
                                && diffLo >= 0 && diffHi <= 1 && diffLo <= diffHi)
                            {
                                difficultyLo = diffLo;
                                difficultyHi = diffHi;
                            }
                            else
                            {
                                Console.WriteLine($"--difficulty needs 'lo,hi' with 0 <= lo <= hi <= 1, got '{args[i + 1]}'");
                                Environment.ExitCode = 1;
                                return;
                            }
                            i++;
                        }
                        break;
                }
            }

            if (engineTheta)
            {
                // θ 只支持方形且 size >= ThetaMinSize；钳制起始尺寸，而不是让每个 size 的批次都失败。
                from = Math.Max(from, Profile.ThetaMinSize);
            }

            Console.WriteLine("=== P1 size sweep (soft-cost blocking) ===");
            Console.WriteLine($"engine: {(engineTheta ? "theta skeleton (W fitted per map)" : "v3 rejection sampling")}"
                              + (profile ? " | profile: on" : "")
                              + (jobs > 1 ? $" | jobs: {jobs}" : ""));
            Console.WriteLine($"sizes {from}x{from} .. {to}x{to} | {count} maps per size | "
                              + $"W = {blockCost} | n = 1..{maxN}");
            if (difficultyLo.HasValue && difficultyHi.HasValue)
            {
                Console.WriteLine($"difficulty target: [{Num(difficultyLo.Value, "F2")}, "
                                  + $"{Num(difficultyHi.Value, "F2")}] "
                                  + (engineTheta
                                      ? "(theta flank-ratio bisection)"
                                      : "(v3 rejection sampling: seeds outside the band are skipped)"));
            }

            Directory.CreateDirectory(directory);

            for (int size = from; size <= to; size++)
            {
                Settings.MapWidth = size;
                Settings.MapHeight = size;

                Console.WriteLine();
                Console.WriteLine($"------------------- {size} x {size} -------------------");

                var batchArgs = new List<string>
                {
                    "sweep",
                    "--count", Num(count),
                    "--nmax", Num(maxN),
                    "--combos", Num(maxCombos),
                    "--seed", Num(unchecked(baseSeed + size * 101)),
                    "--w", Num(blockCost),
                    "--jobs", Num(jobs),
                    "--csv", Path.Combine(directory, $"sweep_{size}x{size}.csv"),
                    "--engine", engineTheta ? "theta" : "v3"
                };
                if (profile) batchArgs.Add("--profile");
                if (difficultyLo.HasValue && difficultyHi.HasValue)
                {
                    batchArgs.Add("--difficulty");
                    batchArgs.Add(Num(difficultyLo.Value, "F6") + "," + Num(difficultyHi.Value, "F6"));
                }

                RunAnalyzerBatch(batchArgs.ToArray());
            }
        }

        private static void WaitForExitKey()
        {
            if (Console.IsInputRedirected) return;
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    // P2：分阶段生成剖析。仅在 --profile 时记录；不消耗随机数，不改变生成结果与 CSV 契约。
    // P6：mutator 全部走单锁 —— 并行批量（--jobs ≥ 2）时各工作线程并发累计，顺序无关；
    // Report() 只在批次结束后由主线程调用，聚合结果与顺序模式一致（分布排序、总和累加）。
    internal static class GenerationProfiler
    {
        public static bool Enabled;
        public static int ParallelJobs = 1;   // P6：报告注明并行度

        private static readonly object Gate = new object();

        public static readonly Dictionary<string, long> V3Rejects = new Dictionary<string, long>();
        public static readonly Dictionary<string, long> ThetaRejects = new Dictionary<string, long>();
        public static readonly Dictionary<string, double> StageMs = new Dictionary<string, double>();
        public static readonly List<double> GenerateMs = new List<double>();
        public static readonly List<double> AnalyzeMs = new List<double>();
        public static readonly List<int> V3Attempts = new List<int>();

        public static long Mark() => Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        public static double Since(long mark)
            => Enabled ? System.Diagnostics.Stopwatch.GetElapsedTime(mark).TotalMilliseconds : 0.0;

        public static void Reject(string reason)
        {
            if (!Enabled) return;
            lock (Gate) V3Rejects[reason] = V3Rejects.GetValueOrDefault(reason) + 1;
        }

        public static void ThetaReject(string reason)
        {
            if (!Enabled) return;
            lock (Gate) ThetaRejects[reason] = ThetaRejects.GetValueOrDefault(reason) + 1;
        }

        public static void AddStage(string key, double ms)
        {
            if (!Enabled) return;
            lock (Gate) StageMs[key] = StageMs.GetValueOrDefault(key) + ms;
        }

        public static void RecordGenerate(long mark, int v3Attempts)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                GenerateMs.Add(Since(mark));
                if (v3Attempts > 0) V3Attempts.Add(v3Attempts);
            }
        }

        public static void RecordAnalyze(long mark)
        {
            if (!Enabled) return;
            lock (Gate) AnalyzeMs.Add(Since(mark));
        }

        private static string Distribution(List<double> values)
        {
            if (values.Count == 0) return "n/a";
            List<double> sorted = values.OrderBy(v => v).ToList();
            double Percentile(double p)
                => sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1))];
            return $"mean {sorted.Average():F1} | median {Percentile(0.5):F1} | p90 {Percentile(0.9):F1} | max {sorted[^1]:F1}";
        }

        public static string Report()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("generation profile (--profile; times in ms)");
            text.AppendLine($"parallel jobs: {ParallelJobs}");
            text.AppendLine($"attempts per generated map (v3): {Distribution(V3Attempts.Select(v => (double)v).ToList())}");
            text.AppendLine($"generate ms/map: {Distribution(GenerateMs)}");
            text.AppendLine($"analyze ms/map: {Distribution(AnalyzeMs)}");
            text.AppendLine("stage totals (s):");
            foreach (KeyValuePair<string, double> pair in StageMs.OrderBy(p => p.Key, StringComparer.Ordinal))
                text.AppendLine($"  {pair.Key}: {pair.Value / 1000.0:F2}");
            text.AppendLine("v3 rejection reasons (first failing constraint per rejected attempt):");
            long v3Total = V3Rejects.Values.Sum();
            foreach (KeyValuePair<string, long> pair in V3Rejects.OrderByDescending(p => p.Value))
                text.AppendLine($"  {pair.Key}: {pair.Value} ({(double)pair.Value / Math.Max(1, v3Total):P1})");
            if (V3Rejects.Count == 0) text.AppendLine("  (none recorded)");
            text.AppendLine("theta failure reasons:");
            foreach (KeyValuePair<string, long> pair in ThetaRejects.OrderByDescending(p => p.Value))
                text.AppendLine($"  {pair.Key}: {pair.Value}");
            if (ThetaRejects.Count == 0) text.AppendLine("  (none recorded)");
            return text.ToString();
        }
    }
}
