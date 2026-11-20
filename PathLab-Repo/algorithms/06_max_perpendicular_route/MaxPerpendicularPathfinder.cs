using System;
using System.Collections.Generic;
using System.Diagnostics;
using PathLab.Core;
using PathLab.Algorithms.ShortestSteps00;

namespace PathLab.Algorithms.MaxPerpendicular06;

/// <summary>How the bounded walk search explores the simple-path space.</summary>
public enum ScoreSearchMode
{
    /// <summary>DFS + backtracking with a safe upper-bound prune; exact when it completes.</summary>
    Exact,

    /// <summary>Beam search: keeps the best partial walks per level; never proves optimal.</summary>
    Beam
}

/// <summary>
/// Maximum weighted perpendicular distance route with respect to a marker point Q.
/// Every move is scored on its own, so no line merging is needed:
///   horizontal move on row y   -> weight |y - qy|
///   vertical move on column x  -> weight |x - qx|
/// The path must be a simple walk from Start to Goal, so the score is not reachable by
/// walking in circles. The space of simple paths is explored by DFS (exact when the
/// node/time budget is enough) or by beam search; a bounded search reports the best
/// route found but never claims it is optimal.
/// </summary>
public sealed class MaxPerpendicularPathfinder
    : IPathfinder, IReferencePointModule, ITransitionAware, ITauntAware
{
    public string Id => "06-max-perpendicular-route";

    public string Name => "最大垂直距离路径";

    public string Summary => "标记点 Q：让路径尽量沿远离 Q 的直线走，最大化 Σ 步长 × 到 Q 所在直线的垂直距离。";

    public SearchObjective Objective => SearchObjective.Score;

    /// <summary>Exact only when the bounded search finishes; each result reports its own flag.</summary>
    public bool Optimal => false;

    /// <summary>Hard cap on expanded walk nodes; the seed route is kept when the cap hits.</summary>
    public int MaxExpandedNodes { get; set; } = 150_000;

    /// <summary>Wall-clock cap in milliseconds; checked every 256 expansions.</summary>
    public int MaxMilliseconds { get; set; } = 300;

    /// <summary>Exact DFS by default; beam search trades the optimality proof for speed.</summary>
    public ScoreSearchMode Mode { get; set; } = ScoreSearchMode.Exact;

    /// <summary>Partial walks kept per level in <see cref="ScoreSearchMode.Beam"/>.</summary>
    public int BeamWidth { get; set; } = 64;

    /// <summary>Marker point Q; null falls back to the map goal.</summary>
    public Cell? ReferencePoint { get; set; }

    /// <summary>Optional map facilities (portals); a jump is one move worth no perpendicular gain.</summary>
    public IPathProbe? Probe { get; set; }

    /// <summary>Optional taunt-point constraint; null or empty keeps the plain score search.</summary>
    public TauntPlan? Taunt { get; set; }

    public Cell? UsedReference { get; private set; }

    /// <summary>This module has no turn badge; the carrier draws only the R marker.</summary>
    public Cell? FarthestPoint => null;

    private GridMap _map = null!;
    private Cell _marker;
    private bool[,] _visited = new bool[0, 0];
    private readonly List<Cell> _path = new();
    private readonly List<Cell> _explored = new();
    private readonly Stopwatch _clock = new();
    private List<Cell> _bestPath = new();
    private long _bestScore;
    private int _maxWeight;
    private int _unvisited;
    private int _expanded;
    private bool _aborted;
    private string _abortReason = string.Empty;

    /// <summary>One move's weight: distance between the move's line and the marker's line.</summary>
    public static int EdgeWeight(Cell from, Cell to, Cell marker)
    {
        // A facility jump has no direction, so it contributes nothing to the score.
        if (Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) != 1) return 0;

        // A horizontal move keeps the row, a vertical move keeps the column.
        return to.Y == from.Y
            ? Math.Abs(from.Y - marker.Y)
            : Math.Abs(from.X - marker.X);
    }

    /// <summary>
    /// Independent score verification: walks the path once and sums edge weights.
    /// It never looks at any accumulator kept by the search.
    /// </summary>
    public static long CalculatePathScore(IReadOnlyList<Cell> path, Cell marker)
    {
        if (path is null || path.Count < 2) return 0;

        long total = 0;
        for (int index = 1; index < path.Count; index++)
            total += EdgeWeight(path[index - 1], path[index], marker);

        return total;
    }

    /// <summary>Number of heading changes along the path; a straight walk has zero.</summary>
    public static int TurnCount(IReadOnlyList<Cell> path)
    {
        if (path is null) return 0;

        int turns = 0;
        for (int index = 2; index < path.Count; index++)
        {
            int dx1 = path[index - 1].X - path[index - 2].X;
            int dy1 = path[index - 1].Y - path[index - 2].Y;
            int dx2 = path[index].X - path[index - 1].X;
            int dy2 = path[index].Y - path[index - 1].Y;

            if (dx1 != dx2 || dy1 != dy2) turns++;
        }

        return turns;
    }

    /// <summary>Total score per move; a zero-move route averages 0 instead of dividing by zero.</summary>
    public static double AverageEdgeWeight(long totalScore, int pathSteps)
        => pathSteps > 0 ? (double)totalScore / pathSteps : 0;

    public SearchResult Search(GridMap map)
    {
        UsedReference = null;
        _bestPath = new List<Cell>();
        _bestScore = 0;
        _explored.Clear();
        _aborted = false;
        _abortReason = string.Empty;

        var result = new SearchResult();
        if (map is null) return Fail(result, "地图为空");

        Cell marker = ReferencePoint ?? map.Goal;
        if (!map.InBounds(marker)) return Fail(result, "标记点超出地图边界");
        UsedReference = marker;

        if (!map.InBounds(map.Start) || !map.InBounds(map.Goal)
            || map.IsWall(map.Start) || map.IsWall(map.Goal))
            return Fail(result, "出怪口或入怪口不可通行");

        // A shortest route is always a legal simple walk, so a bounded search still has
        // a real route to report instead of inventing one.
        SearchResult seed = new ShortestStepsPathfinder { Probe = Probe, Taunt = Taunt }.Search(map);
        if (!seed.Found)
        {
            result.TerminationReason = "不存在合法路径";
            return Fail(result, "不存在合法路径");
        }

        _map = map;
        _marker = marker;
        _bestPath = new List<Cell>(seed.Path);
        _bestScore = CalculatePathScore(_bestPath, marker);
        _maxWeight = MaxEdgeWeight(map, marker);

        _clock.Restart();

        // Start == Goal: the only simple walk is the single start cell, so it is trivially optimal.
        if (map.Start == map.Goal)
        {
            _explored.Add(map.Start);
        }
        else if (Mode == ScoreSearchMode.Beam)
            RunBeam();
        else
            RunExact();

        _clock.Stop();

        result.Found = true;
        result.Path = _bestPath;
        result.Cost = Price(map, _bestPath);
        result.Score = _bestScore;
        result.Explored = _explored;
        result.IsOptimal = !_aborted && (map.Start == map.Goal || Mode == ScoreSearchMode.Exact);
        result.TerminationReason = _aborted ? _abortReason
            : map.Start == map.Goal ? "找到精确最优路径"
            : Mode == ScoreSearchMode.Beam ? $"近似搜索 · 束宽 {BeamWidth}（未证明最优）"
            : "找到精确最优路径";
        result.ElapsedMs = _clock.Elapsed.TotalMilliseconds;
        result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
        result.TauntVisitedCount = Taunt?.Effective.Count(point => result.Path.Contains(point.Position)) ?? 0;
        return result;
    }

    /// <summary>True when this simple walk already contains every effective taunt point.</summary>
    private bool TauntComplete(IReadOnlyList<Cell> path)
        => Taunt is null || Taunt.Effective.All(point => path.Contains(point.Position));

    private bool TauntComplete() => TauntComplete(_path);

    private static SearchResult Fail(SearchResult result, string note)
    {
        result.Found = false;
        result.Note = note;
        result.Score = 0;
        result.IsOptimal = false;
        return result;
    }

    private static int Price(GridMap map, List<Cell> path)
    {
        int total = 0;
        for (int index = 1; index < path.Count; index++)
            total += map.CostAt(path[index]);

        return total;
    }

    /// <summary>Heaviest possible single move on this map, used by the safe prune.</summary>
    private static int MaxEdgeWeight(GridMap map, Cell marker)
    {
        int rows = Math.Max(Math.Abs(marker.Y), Math.Abs(map.Height - 1 - marker.Y));
        int columns = Math.Max(Math.Abs(marker.X), Math.Abs(map.Width - 1 - marker.X));
        return Math.Max(rows, columns);
    }

    private void RunExact()
    {
        _expanded = 0;
        _visited = new bool[_map.Width, _map.Height];

        int walkable = 0;
        for (int y = 0; y < _map.Height; y++)
        for (int x = 0; x < _map.Width; x++)
            if (!_map.IsWall(new Cell(x, y))) walkable++;

        _unvisited = Math.Max(0, walkable - 1);
        _path.Clear();
        _path.Add(_map.Start);
        _visited[_map.Start.X, _map.Start.Y] = true;

        Walk(_map.Start, 0);
    }

    /// <summary>
    /// Depth-first enumeration of simple walks. Pruning is safe: no remaining move is
    /// heavier than <see cref="_maxWeight"/>, and a simple walk can use at most one
    /// unvisited cell per remaining move, so the bound cannot hide a better route.
    /// </summary>
    private void Walk(Cell current, long score)
    {
        if (_aborted) return;

        if (current == _map.Goal)
        {
            // A goal reached before every effective taunt point is not a legal end.
            if (TauntComplete()
                && (score > _bestScore || (score == _bestScore && _path.Count < _bestPath.Count)))
            {
                _bestScore = score;
                _bestPath = new List<Cell>(_path);
            }

            return;
        }

        _explored.Add(current);
        _expanded++;

        if (_expanded > MaxExpandedNodes)
        {
            Abort("达到搜索节点上限（未证明最优）");
            return;
        }

        if ((_expanded & 255) == 0 && _clock.ElapsedMilliseconds >= MaxMilliseconds)
        {
            Abort("搜索超时（未证明最优）");
            return;
        }

        if (score + (long)_maxWeight * _unvisited < _bestScore) return;

        // One-way passages read the walk backwards: a walked-in passage leaves
        // straight through only, and no jump may replace that committed exit.
        Cell entry = _path.Count >= 2 ? _path[_path.Count - 2] : current;
        var moves = new List<(Cell Cell, int Weight)>(8);

        foreach (Cell next in GeometricExits(entry, current))
        {
            if (_map.IsWall(next) || _visited[next.X, next.Y]) continue;
            moves.Add((next, EdgeWeight(current, next, _marker)));
        }

        // Facility jumps are legal moves worth no perpendicular gain.
        if (Probe is not null && !Committed(entry, current))
            foreach (SpecialTransition jump in Probe.SpecialTransitions(current))
            {
                Cell next = jump.Destination;
                if (_map.IsWall(next) || _visited[next.X, next.Y]) continue;
                moves.Add((next, 0));
            }

        // Heavier moves first, then row order: a good route is found early and the
        // score bound starts pruning soon. The order never changes the optimum.
        for (int i = 1; i < moves.Count; i++)
        {
            (Cell Cell, int Weight) move = moves[i];
            int j = i - 1;

            while (j >= 0 && Before(move, moves[j]))
            {
                moves[j + 1] = moves[j];
                j--;
            }

            moves[j + 1] = move;
        }

        for (int i = 0; i < moves.Count; i++)
        {
            Cell next = moves[i].Cell;
            _visited[next.X, next.Y] = true;
            _path.Add(next);
            _unvisited--;

            Walk(next, score + moves[i].Weight);

            _unvisited++;
            _path.RemoveAt(_path.Count - 1);
            _visited[next.X, next.Y] = false;

            if (_aborted) return;
        }
    }

    private IEnumerable<Cell> GeometricExits(Cell entry, Cell cell)
        => Probe is null ? _map.Neighbours(cell) : Probe.ExitsAfter(entry, cell);

    /// <summary>True when the walk entered a passage cell by foot, so only its straight exit remains.</summary>
    private bool Committed(Cell entry, Cell cell)
        => Probe is not null && Probe.IsOneWay(cell)
           && Math.Abs(entry.X - cell.X) + Math.Abs(entry.Y - cell.Y) == 1;

    private static bool Before((Cell Cell, int Weight) left, (Cell Cell, int Weight) right)
    {
        if (left.Weight != right.Weight) return left.Weight > right.Weight;
        if (left.Cell.Y != right.Cell.Y) return left.Cell.Y < right.Cell.Y;
        return left.Cell.X < right.Cell.X;
    }

    /// <summary>
    /// Level-synchronous beam search over partial simple walks. The result is always
    /// reported as approximate: a pruned level can hide the global maximum.
    /// </summary>
    private void RunBeam()
    {
        _expanded = 0;

        var frontier = new List<Partial> { new Partial(_map, _map.Start) };

        while (frontier.Count > 0 && !_aborted)
        {
            var next = new List<Partial>();

            foreach (Partial walk in frontier)
            {
                if (_aborted) break;

                _explored.Add(walk.Head);
                _expanded++;
                if (_expanded > MaxExpandedNodes)
                {
                    Abort("达到搜索节点上限（未证明最优）· 近似搜索");
                    break;
                }

                if ((_expanded & 255) == 0 && _clock.ElapsedMilliseconds >= MaxMilliseconds)
                {
                    Abort("搜索超时（未证明最优）· 近似搜索");
                    break;
                }

                // One-way passages read the walk backwards: a walked-in passage
                // leaves straight through only, and no jump may replace that exit.
                Cell entry = walk.Path.Count >= 2 ? walk.Path[walk.Path.Count - 2] : walk.Head;

                foreach (Cell cell in GeometricExits(entry, walk.Head))
                {
                    if (_map.IsWall(cell) || walk.Visited[cell.X, cell.Y]) continue;

                    Partial child = walk.Step(cell, EdgeWeight(walk.Head, cell, _marker));
                    if (cell == _map.Goal)
                    {
                        if (TauntComplete(child.Path)
                            && (child.Score > _bestScore
                                || (child.Score == _bestScore && child.Path.Count < _bestPath.Count)))
                        {
                            _bestScore = child.Score;
                            _bestPath = child.Path;
                        }

                        continue;
                    }

                    next.Add(child);
                }

                if (Probe is not null && !Committed(entry, walk.Head))
                    foreach (SpecialTransition jump in Probe.SpecialTransitions(walk.Head))
                    {
                        Cell cell = jump.Destination;
                        if (_map.IsWall(cell) || walk.Visited[cell.X, cell.Y]) continue;

                        Partial child = walk.Step(cell, 0);
                        if (cell == _map.Goal)
                        {
                            if (TauntComplete(child.Path)
                                && (child.Score > _bestScore
                                    || (child.Score == _bestScore && child.Path.Count < _bestPath.Count)))
                            {
                                _bestScore = child.Score;
                                _bestPath = child.Path;
                            }

                            continue;
                        }

                        next.Add(child);
                    }
            }

            if (_aborted) break;

            next.Sort(static (left, right) =>
            {
                if (left.Score != right.Score) return right.Score.CompareTo(left.Score);
                if (left.Path.Count != right.Path.Count) return left.Path.Count.CompareTo(right.Path.Count);
                if (left.Head.Y != right.Head.Y) return left.Head.Y.CompareTo(right.Head.Y);
                return left.Head.X.CompareTo(right.Head.X);
            });

            if (next.Count > BeamWidth) next.RemoveRange(BeamWidth, next.Count - BeamWidth);
            frontier = next;
        }
    }

    private void Abort(string reason)
    {
        _aborted = true;
        _abortReason = reason;
    }

    private sealed class Partial
    {
        public Partial(GridMap map, Cell start)
        {
            Path = new List<Cell> { start };
            Visited = new bool[map.Width, map.Height];
            Visited[start.X, start.Y] = true;
            Head = start;
            Score = 0;
        }

        private Partial(List<Cell> path, bool[,] visited, Cell head, long score)
        {
            Path = path;
            Visited = visited;
            Head = head;
            Score = score;
        }

        public List<Cell> Path { get; }

        public bool[,] Visited { get; }

        public Cell Head { get; }

        public long Score { get; }

        /// <summary>Child state with its own visited set, so siblings never share writes.</summary>
        public Partial Step(Cell next, int weight)
        {
            var path = new List<Cell>(Path) { next };
            var visited = (bool[,])Visited.Clone();
            visited[next.X, next.Y] = true;
            return new Partial(path, visited, next, Score + weight);
        }
    }
}
