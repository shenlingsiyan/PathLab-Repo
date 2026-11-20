using System;
using System.Collections.Generic;
using PathLab.Algorithms.ShortestSteps00;
using PathLab.Core;

namespace PathLab.Algorithms.RadiusCandidates04;

/// <summary>
/// Target-candidate search for a special enemy:
/// 1. L = fewest moves from the spawn to the entrance (unweighted BFS, 4-neighbour);
/// 2. R = floor(L * alpha), no clamp, R = 0 is legal;
/// 3. keep every cell whose exact Euclidean distance to the entrance lies between the
///    integer bounds (equal bounds = one circle, different bounds = a ring);
/// 4. drop walls and cells that cannot reach the entrance.
/// Distances use integer squared coordinates (no square root, no float compare).
/// The band is checked against the entrance, not against walk distance.
/// </summary>
public sealed class RadiusCandidateFinder : ICandidateTargetFinder
{
    public const double InitialAlpha = 0.5;

    public string Id => "04-radius-candidates";

    public string Name => "半径候选目标点";

    public string Summary => "以出怪口到入怪口的最短步数 × alpha 为基准半径（向下取整），"
                             + "在整数的半径区间内返回入怪口周围可通行且可达的格子"
                             + "（两端相同为圆周，不同为圆环）。";

    public double DefaultAlpha => InitialAlpha;

    /// <summary>Exact circle test: <c>dx² + dy² == radius²</c> in integers.</summary>
    public static bool IsOnCircle(Cell entrance, Cell cell, int radius)
    {
        return SquaredDistance(entrance, cell) == (long)radius * radius;
    }

    /// <summary>Closed integer band test: <c>min² &lt;= dx² + dy² &lt;= max²</c>.</summary>
    public static bool IsInBand(Cell entrance, Cell cell, int minRadius, int maxRadius)
    {
        long squared = SquaredDistance(entrance, cell);
        return squared >= (long)minRadius * minRadius && squared <= (long)maxRadius * maxRadius;
    }

    public CandidateTargetsResult Find(GridMap map, Cell entrance, double alpha,
        int? minRadius = null, int? maxRadius = null)
    {
        var result = new CandidateTargetsResult { Alpha = alpha };

        if (map is null)
        {
            result.Error = CandidateTargetError.NullMap;
            result.Note = "地图为空";
            return result;
        }

        if (!map.InBounds(entrance))
        {
            result.Error = CandidateTargetError.EntranceOutOfBounds;
            result.Note = "入怪口超出地图边界";
            return result;
        }

        if (double.IsNaN(alpha) || double.IsInfinity(alpha) || alpha < 0)
        {
            result.Error = CandidateTargetError.InvalidAlpha;
            result.Note = "比例参数必须是非负有限数值";
            return result;
        }

        if (!IsValidRange(minRadius, maxRadius))
        {
            result.Error = CandidateTargetError.InvalidRadiusRange;
            result.Note = "半径区间必须是非负整数，且下界不大于上界";
            return result;
        }

        // L: reuse the unweighted BFS module, so moves count edges and prices are ignored.
        GridMap probe = map.Clone();
        probe.Goal = entrance;
        SearchResult route = new ShortestStepsPathfinder().Search(probe);

        if (!route.Found)
        {
            result.Error = CandidateTargetError.Unreachable;
            result.Note = "出怪口无法到达入怪口";
            return result;
        }

        int steps = route.Path.Count - 1;
        result.BasePathSteps = steps;

        double scaled = steps * alpha;
        if (scaled > int.MaxValue)
        {
            result.Error = CandidateTargetError.RadiusOverflow;
            result.Note = "半径超出可表示范围";
            return result;
        }

        int radius = (int)Math.Floor(scaled);
        result.Radius = radius;

        // Null ends follow the computed base radius, so the default query stays a circle.
        int low = minRadius ?? radius;
        int high = maxRadius ?? radius;

        if (low > high)
        {
            result.Error = CandidateTargetError.InvalidRadiusRange;
            result.Note = "半径区间的下界大于上界";
            return result;
        }

        result.MinRadius = low;
        result.MaxRadius = high;

        bool[,] reachable = ReachableFrom(map, entrance);
        long squaredLow = (long)low * low;
        long squaredHigh = (long)high * high;

        // Row-major scan keeps candidate order deterministic.
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            var cell = new Cell(x, y);
            if (map.IsWall(cell) || !reachable[x, y]) continue;

            long squared = SquaredDistance(entrance, cell);
            if (squared < squaredLow || squared > squaredHigh) continue;

            result.Candidates.Add(cell);
        }

        result.Success = true;
        return result;
    }

    /// <summary>Bounds must be non-negative and ordered; nothing is swapped or clamped.</summary>
    private static bool IsValidRange(int? minRadius, int? maxRadius)
    {
        if (minRadius is < 0 || maxRadius is < 0) return false;
        if (minRadius is { } low && maxRadius is { } high) return low <= high;

        return true;
    }

    private static long SquaredDistance(Cell entrance, Cell cell)
    {
        long dx = (long)cell.X - entrance.X;
        long dy = (long)cell.Y - entrance.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// One BFS from the entrance over traversable cells. This project's 4-neighbour moves
    /// are reversible, so this set is exactly the cells able to walk back to the entrance.
    /// </summary>
    private static bool[,] ReachableFrom(GridMap map, Cell entrance)
    {
        bool[,] reachable = new bool[map.Width, map.Height];
        if (map.IsWall(entrance)) return reachable;

        var queue = new Queue<Cell>();
        reachable[entrance.X, entrance.Y] = true;
        queue.Enqueue(entrance);

        while (queue.Count > 0)
        {
            Cell current = queue.Dequeue();
            foreach (Cell next in map.Neighbours(current))
            {
                if (map.IsWall(next) || reachable[next.X, next.Y]) continue;

                reachable[next.X, next.Y] = true;
                queue.Enqueue(next);
            }
        }

        return reachable;
    }
}
