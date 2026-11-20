using System.Collections.Generic;
using PathLab.Core;

namespace PathLab.Algorithms.MinCost01;

/// <summary>
/// Weighted 4-neighbour Dijkstra over cell entry prices.
/// Non-negative prices, one uniform price per move, no heuristic.
/// Tie-break is (total cost, y, x), so the same map always yields the same path.
///
/// With a probe, a teleport is priced as the portal fee plus the price of the cell
/// it lands on, which mirrors "entering a cell pays its price" for the exit cell.
///
/// One-way passages split the search state: the state is the pair (cell, entry),
/// where entry is meaningful only on passage cells (a walk-in entry must leave
/// straight through). Every other cell collapses to a single state.
///
/// Taunt points add a visited mask to the state: (cell, entry, mask). Entering an
/// effective point sets its bit, and only a goal state with every bit set counts as
/// reached. Prices are untouched, so Dijkstra still minimises the total entry cost
/// among the routes that satisfy the constraint.
/// </summary>
public sealed class MinCostPathfinder : IPathfinder, ITransitionAware, ITauntAware
{
    public string Id => "01-min-cost";

    public string Name => "最小代价 (Dijkstra)";

    public string Summary => "加权 Dijkstra：最小化进入代价之和，步数可能更多。";

    public SearchObjective Objective => SearchObjective.Cost;

    public bool Optimal => true;

    /// <summary>Optional map facilities (portals); null keeps the plain 4-neighbour search.</summary>
    public IPathProbe? Probe { get; set; }

    /// <summary>Optional taunt-point constraint; null or empty keeps the plain search.</summary>
    public TauntPlan? Taunt { get; set; }

    /// <summary>One search state: the cell, the entry cell and the visited-taunt mask.</summary>
    private readonly record struct Step(Cell Here, Cell From, int Mask);

    public SearchResult Search(GridMap map)
    {
        var result = new SearchResult();
        var best = new Dictionary<Step, int>();
        var previous = new Dictionary<Step, Step>();
        var seen = new HashSet<Cell>();
        var explored = new List<Cell>();

        // Priority tuple mirrors the project-wide (cost, y, x) tie-break.
        var queue = new PriorityQueue<Step, (int Cost, int Y, int X)>();

        var start = new Step(map.Start, map.Start, Mark(0, map.Start));
        best[start] = 0;
        queue.Enqueue(start, (0, map.Start.Y, map.Start.X));

        Step? goalState = null;

        while (queue.TryDequeue(out Step current, out (int Cost, int Y, int X) priority))
        {
            if (priority.Cost > best[current]) continue;

            if (seen.Add(current.Here)) explored.Add(current.Here);

            // The goal only counts when every effective taunt point was visited.
            if (current.Here == map.Goal && Complete(current.Mask))
            {
                goalState = current;
                break;
            }

            foreach (Cell next in Exits(map, current))
            {
                int step = map.CostAt(next);
                if (step == GridMap.Wall) continue;

                // Only a walked-in entry shapes a passage cell's future exits; every
                // exit is a 4-neighbour move, so the edge itself is always a walk.
                Relax(new Step(next,
                        Probe is not null && Probe.IsOneWay(next) ? current.Here : next,
                        Mark(current.Mask, next)),
                    step, current, priority.Cost);
            }

            // Walking into a passage commits to the straight exit, so no jump may
            // replace it; an arrival without geometry keeps jumps available.
            if (Probe is null || Committed(current)) continue;

            foreach (SpecialTransition jump in Probe.SpecialTransitions(current.Here))
            {
                int step = map.CostAt(jump.Destination);
                if (step == GridMap.Wall) continue;

                // no geometric entry; a jump landing on a taunt point counts as visited
                Relax(new Step(jump.Destination, jump.Destination, Mark(current.Mask, jump.Destination)),
                    jump.Cost + step, current, priority.Cost);
            }
        }

        void Relax(Step state, int step, Step from, int known)
        {
            int candidate = known + step;
            if (best.TryGetValue(state, out int incumbent) && incumbent <= candidate) return;

            best[state] = candidate;
            previous[state] = from;
            queue.Enqueue(state, (candidate, state.Here.Y, state.Here.X));
        }

        if (goalState is not { } reached)
        {
            result.Found = false;
            result.Explored = explored;
            result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
            result.MissedTauntCells = Taunt is null
                ? new List<Cell>()
                : new List<Cell>(Taunt.Missing(0).Select(point => point.Position));
            result.Note = Taunt is { HasAny: true }
                ? $"无法同时经过全部 {Taunt.Effective.Count} 个嘲讽点"
                : "目标不可达";
            return result;
        }

        var path = new List<Cell>();
        Step cursor = reached;

        while (true)
        {
            path.Add(cursor.Here);
            if (cursor.Here == map.Start) break;

            if (!previous.TryGetValue(cursor, out Step parent))
            {
                result.Found = false;
                result.Explored = explored;
                result.Note = "路径重建失败";
                return result;
            }

            cursor = parent;
        }

        path.Reverse();

        result.Found = true;
        result.Cost = best[reached];
        result.Path = path;
        result.Explored = explored;
        result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
        result.TauntVisitedCount = Taunt?.CountVisited(reached.Mask) ?? 0;
        return result;
    }

    /// <summary>Marks the taunt point on this cell, if any, into the visited mask.</summary>
    private int Mark(int mask, Cell cell) => Taunt?.Mark(mask, cell) ?? mask;

    /// <summary>True when every effective taunt point has been visited.</summary>
    private bool Complete(int mask) => Taunt is null || Taunt.Complete(mask);

    /// <summary>True when the state walked into a passage cell, so only its straight exit remains.</summary>
    private bool Committed(Step state)
        => Probe is not null && Probe.IsOneWay(state.Here) && WalkedIn(state, state.Here);

    /// <summary>True when the state arrived at its cell by a 4-neighbour move.</summary>
    private bool WalkedIn(Step state, Cell cell)
        => Math.Abs(state.From.X - cell.X) + Math.Abs(state.From.Y - cell.Y) == 1;

    /// <summary>Entry-aware exits: plain neighbours without a probe, facility exits with one.</summary>
    private IEnumerable<Cell> Exits(GridMap map, Step state)
        => Probe is null ? map.Neighbours(state.Here) : Probe.ExitsAfter(state.From, state.Here);
}
