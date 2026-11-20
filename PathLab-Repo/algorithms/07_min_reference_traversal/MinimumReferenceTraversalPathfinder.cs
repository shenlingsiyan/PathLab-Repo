using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PathLab.Core;

namespace PathLab.Algorithms.MinReference07;

/// <summary>
/// Soft-obstacle route over a set of reference cells.
///
/// Reference cells stay walkable, but entering one is the *primary* cost, so a route
/// that avoids every reference always beats a shorter route that crosses one.
/// A lexicographic Dijkstra over the pair (crossings, moves) solves this exactly:
/// every edge adds 0 or 1 crossings and exactly 1 move, both non-negative, and the
/// dictionary order is preserved by addition, so the first pop of the goal is optimal.
///
/// Ordinary walls stay impassable, and the start/goal never count as a crossing
/// because they are fixed gates rather than waypoints the route chose to enter.
///
/// One-way passages split the search state into (cell, entry): a walk-in entry must
/// leave straight through, so crossing a passage is one straight line, while a jump
/// arrival may leave anywhere except the tail side.
///
/// Taunt points add a visited mask to the state: (cell, entry, mask). The lexicographic
/// objective is untouched; the constraint only decides which goal states count. A route
/// that must collect points may revisit a cell, so the path audit allows repeats while
/// the constraint is active.
/// </summary>
public sealed class MinimumReferenceTraversalPathfinder
    : IPathfinder, IReferenceCellSetModule, ITransitionAware, ITauntAware
{
    public string Id => "07-min-reference-traversal";

    public string Name => "最少参考点穿越最短路径";

    public string Summary => "参考点是软障碍：优先 0 次穿越，其次穿越最少，再取步数最短（字典序 Dijkstra）。";

    /// <summary>The tie-breaker after crossings is move count, so steps is the closest metric.</summary>
    public SearchObjective Objective => SearchObjective.Steps;

    /// <summary>Optimal for the lexicographic objective, but only under the reference constraint.</summary>
    public bool Optimal => true;

    public bool Constrained => true;

    public IReadOnlyList<Cell> ReferenceCells { get; set; } = Array.Empty<Cell>();

    public IReadOnlyList<Cell> UsedReferenceCells { get; private set; } = Array.Empty<Cell>();

    /// <summary>Optional map facilities (portals); a jump costs one move and may land on a reference.</summary>
    public IPathProbe? Probe { get; set; }

    /// <summary>Optional taunt-point constraint; null or empty keeps the plain search.</summary>
    public TauntPlan? Taunt { get; set; }

    /// <summary>One search state: the cell, the entry cell and the visited-taunt mask.</summary>
    private readonly record struct Step(Cell Here, Cell From, int Mask);

    public SearchResult Search(GridMap map)
    {
        UsedReferenceCells = Array.Empty<Cell>();

        if (map is null) return Fail("地图为空", InvalidInput);
        if (!map.InBounds(map.Start)) return Fail("起点超出地图边界", InvalidInput);
        if (!map.InBounds(map.Goal)) return Fail("终点超出地图边界", InvalidInput);
        if (map.IsWall(map.Start)) return Fail("起点不可通行", InvalidInput);
        if (map.IsWall(map.Goal)) return Fail("终点不可通行", InvalidInput);

        var references = new HashSet<Cell>();
        foreach (Cell cell in ReferenceCells ?? Array.Empty<Cell>())
        {
            if (!map.InBounds(cell))
                return Fail($"参考点超出地图边界：({cell.X}, {cell.Y})", InvalidInput);

            references.Add(cell); // 重复坐标只留一份；落在墙上的参考点保留，但它永远进不去
        }

        UsedReferenceCells = Sorted(references);

        var clock = Stopwatch.StartNew();
        var result = new SearchResult();

        if (map.Start == map.Goal)
        {
            // A zero-move route crosses nothing by definition.
            result.Found = true;
            result.Path = new List<Cell> { map.Start };
            result.Cost = 0;
            result.ReferenceCount = 0;
            result.IsOptimal = true;
            result.TerminationReason = FoundOptimal;
            result.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            return result;
        }

        var best = new Dictionary<Step, (int Crossings, int Moves)>();
        var previous = new Dictionary<Step, Step>();
        var explored = new List<Cell>();
        var seen = new HashSet<Cell>();

        // Priority = (crossings, moves, y, x): the trailing pair only makes ties deterministic.
        var queue = new PriorityQueue<Step, (int Crossings, int Moves, int Y, int X)>();

        var origin = new Step(map.Start, map.Start, Mark(0, map.Start));
        best[origin] = (0, 0);
        queue.Enqueue(origin, (0, 0, map.Start.Y, map.Start.X));

        bool reached = false;
        Step? goalState = null;

        while (queue.TryDequeue(out Step current, out (int Crossings, int Moves, int Y, int X) key))
        {
            // A state may sit in the heap several times; ignore entries that lost to a later cost.
            if (!best.TryGetValue(current, out (int Crossings, int Moves) known)
                || known.Crossings != key.Crossings
                || known.Moves != key.Moves)
                continue;

            if (seen.Add(current.Here)) explored.Add(current.Here);

            // The goal only counts when every effective taunt point was visited.
            if (current.Here == map.Goal && Complete(current.Mask))
            {
                reached = true;
                goalState = current;
                break;
            }

            // Fixed order (right, left, down, up); walls are the only impassable cells.
            foreach (Cell next in GeometricExits(map, current))
            {
                if (map.IsWall(next)) continue;

                // Only a walked-in entry shapes a passage cell's future exits; every
                // exit is a 4-neighbour move, so the edge itself is always a walk.
                Offer(current,
                    new Step(next, Probe is not null && Probe.IsOneWay(next) ? current.Here : next,
                        Mark(current.Mask, next)),
                    key.Crossings + Crossing(map, next, references), key.Moves + 1);
            }

            // A teleport is one move; landing on a reference still counts as entering it.
            // Walking into a passage commits to the straight exit, so no jump may replace
            // it; an arrival without geometry keeps jumps available.
            if (Probe is null || Committed(current)) continue;

            foreach (SpecialTransition jump in Probe.SpecialTransitions(current.Here))
            {
                if (map.IsWall(jump.Destination)) continue;

                // no geometric entry; a jump landing on a taunt point counts as visited
                Offer(current,
                    new Step(jump.Destination, jump.Destination, Mark(current.Mask, jump.Destination)),
                    key.Crossings + Crossing(map, jump.Destination, references), key.Moves + 1);
            }
        }

        void Offer(Step from, Step state, int crossings, int moves)
        {
            if (best.TryGetValue(state, out (int Crossings, int Moves) incumbent)
                && (incumbent.Crossings, incumbent.Moves).CompareTo((crossings, moves)) <= 0)
                return;

            best[state] = (crossings, moves);
            previous[state] = from;
            queue.Enqueue(state, (crossings, moves, state.Here.Y, state.Here.X));
        }

        if (!reached || goalState is not { } reachedState)
        {
            result.Found = false;
            result.Note = Taunt is { HasAny: true }
                ? $"无法同时经过全部 {Taunt.Effective.Count} 个嘲讽点"
                : NoRoute;
            result.TerminationReason = NoRoute;
            result.Explored = explored;
            result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
            result.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            return result;
        }

        var path = new List<Cell>();
        Step cursor = reachedState;

        while (true)
        {
            path.Add(cursor.Here);
            if (cursor.Here == map.Start) break;

            if (!previous.TryGetValue(cursor, out Step parent))
                return Fail("路径重建失败", ReconstructionFailed);

            cursor = parent;
        }

        path.Reverse();

        // Rebuilding is the only place a bug could invent an illegal walk, so verify it.
        string? problem = CheckPath(path, map, Probe, Taunt is { HasAny: true });
        if (problem is not null) return Fail(problem, ReconstructionFailed);

        result.Found = true;
        result.Path = path;
        result.Cost = Price(map, path);
        result.Explored = explored;
        result.ReferenceCount = CountReferences(path, map, UsedReferenceCells);
        result.IsOptimal = true;
        result.TerminationReason = FoundOptimal;
        result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
        result.TauntVisitedCount = Taunt?.CountVisited(reachedState.Mask) ?? 0;
        result.ElapsedMs = clock.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Marks the taunt point on this cell, if any, into the visited mask.</summary>
    private int Mark(int mask, Cell cell) => Taunt?.Mark(mask, cell) ?? mask;

    /// <summary>True when every effective taunt point has been visited.</summary>
    private bool Complete(int mask) => Taunt is null || Taunt.Complete(mask);

    /// <summary>Reference cells the path enters, counted from the path itself; start/goal excluded.</summary>
    public static int CountReferences(IReadOnlyList<Cell> path, GridMap map, IReadOnlyList<Cell> referencePoints)
    {
        if (path is null || map is null) return 0;

        var references = new HashSet<Cell>();
        foreach (Cell cell in referencePoints ?? Array.Empty<Cell>())
            if (map.InBounds(cell))
                references.Add(cell);

        int count = 0;
        foreach (Cell cell in path)
        {
            if (cell == map.Start || cell == map.Goal) continue;
            if (references.Contains(cell)) count++;
        }

        return count;
    }

    /// <summary>
    /// Independent check of a finished result: re-walks the path and recomputes the
    /// crossings instead of trusting the search. Returns null when everything agrees,
    /// otherwise the first problem found.
    /// </summary>
    public static string? ValidatePathfindingResult(
        SearchResult? result, GridMap? map, IReadOnlyList<Cell>? referencePoints,
        IPathProbe? probe = null, bool allowRepeats = false)
    {
        if (map is null) return "地图为空";
        if (result is null) return "结果为空";

        if (!result.Found)
            return result.Note is null ? "失败结果缺少原因" : null;

        string? problem = CheckPath(result.Path, map, probe, allowRepeats);
        if (problem is not null) return problem;

        int count = CountReferences(result.Path, map, referencePoints ?? Array.Empty<Cell>());
        return count == result.ReferenceCount
            ? null
            : $"穿越数量不一致：报告 {result.ReferenceCount}，实际 {count}";
    }

    /// <summary>A move is legal when it is 4-adjacent, a jump the probe offers, and no
    /// passage crossing ever turns: a walked-in passage must leave straight through,
    /// and a jump landing on a passage may not leave toward the tail side.</summary>
    private static string? CheckPath(List<Cell> path, GridMap map, IPathProbe? probe,
        bool allowRepeats = false)
    {
        if (path.Count == 0) return "路径为空";
        if (path[0] != map.Start) return "路径未从起点出发";
        if (path[^1] != map.Goal) return "路径未到达终点";

        var seen = new HashSet<Cell>();

        for (int index = 0; index < path.Count; index++)
        {
            Cell cell = path[index];

            if (!seen.Add(cell) && !allowRepeats) return $"路径重复经过 ({cell.X}, {cell.Y})";
            if (!map.InBounds(cell)) return $"路径越界 ({cell.X}, {cell.Y})";
            if (map.IsWall(cell)) return $"路径穿过障碍 ({cell.X}, {cell.Y})";

            if (index > 0)
            {
                Cell from = path[index - 1];
                bool walked = Math.Abs(cell.X - from.X) + Math.Abs(cell.Y - from.Y) == 1;
                if (!walked && !IsJump(probe, from, cell))
                    return "路径存在不相邻的连续节点";
            }

            if (index > 0 && index + 1 < path.Count && probe is not null && probe.IsOneWay(cell)
                && !probe.ExitsAfter(path[index - 1], cell).Contains(path[index + 1]))
                return $"路径在单向通道 ({cell.X}, {cell.Y}) 内拐弯";
        }

        return null;
    }

    /// <summary>True when the probe really offers this jump, so a gap in the path is explained.</summary>
    private static bool IsJump(IPathProbe? probe, Cell from, Cell to)
    {
        if (probe is null) return false;

        foreach (SpecialTransition jump in probe.SpecialTransitions(from))
            if (jump.Destination == to)
                return true;

        return false;
    }

    private IEnumerable<Cell> GeometricExits(GridMap map, Step state)
        => Probe is null ? map.Neighbours(state.Here) : Probe.ExitsAfter(state.From, state.Here);

    /// <summary>True when the state walked into a passage cell, so only its straight exit remains.</summary>
    private bool Committed(Step state)
        => Probe is not null && Probe.IsOneWay(state.Here)
           && Math.Abs(state.From.X - state.Here.X) + Math.Abs(state.From.Y - state.Here.Y) == 1;

    private static int Crossing(GridMap map, Cell cell, HashSet<Cell> references)
        => references.Contains(cell) && cell != map.Start && cell != map.Goal ? 1 : 0;

    private static Cell[] Sorted(HashSet<Cell> cells)
        => cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();

    private static int Price(GridMap map, List<Cell> path)
    {
        int total = 0;
        for (int index = 1; index < path.Count; index++)
            total += map.CostAt(path[index]);

        return total;
    }

    private const string FoundOptimal = "找到最少穿越的最短路径";
    private const string NoRoute = "不存在合法路径";
    private const string InvalidInput = "输入无效";
    private const string ReconstructionFailed = "路径重建校验失败";

    private static SearchResult Fail(string note, string reason) => new()
    {
        Found = false,
        Note = note,
        TerminationReason = reason,
        IsOptimal = false
    };
}
