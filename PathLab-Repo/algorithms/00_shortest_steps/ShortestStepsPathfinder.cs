using System.Collections.Generic;
using PathLab.Core;

namespace PathLab.Algorithms.ShortestSteps00;

/// <summary>
/// Breadth-first search over move count.
/// Cell prices are ignored (only walls block), so the returned path has the
/// fewest moves; its total price can be arbitrarily higher than the cheapest path.
///
/// When the carrier supplies a probe, facility jumps (teleports) join the frontier as
/// ordinary one-move edges, so a portal costs exactly one step.
///
/// One-way passages split the search state: the state is the pair (cell, entry), where
/// entry is meaningful only on passage cells (a walk-in entry must leave straight
/// through, so a passage reached from the tail differs from the same cell reached
/// from a side). Every other cell collapses to a single state.
///
/// Taunt points add a visited mask to the state: (cell, entry, mask). A step that lands
/// on an effective point sets its bit, and only a goal state with every bit set counts
/// as reached. The move cost is unchanged, so BFS still returns the fewest moves among
/// the routes that satisfy the constraint.
/// </summary>
public sealed class ShortestStepsPathfinder : IPathfinder, ITransitionAware, ITauntAware
{
    public string Id => "00-shortest-steps";

    public string Name => "最短路程 (BFS)";

    public string Summary => "最短步数 BFS：忽略格子代价，只保证步数最少。";

    public SearchObjective Objective => SearchObjective.Steps;

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
        var previous = new Dictionary<Step, Step>();
        var visited = new HashSet<Step>();
        var seen = new HashSet<Cell>();
        var explored = new List<Cell>();
        var queue = new Queue<Step>();

        var start = new Step(map.Start, map.Start, Mark(0, map.Start));
        visited.Add(start);
        queue.Enqueue(start);
        bool reached = false;
        Step goalState = start;

        while (queue.Count > 0)
        {
            Step current = queue.Dequeue();
            if (seen.Add(current.Here)) explored.Add(current.Here);

            // The goal only counts when every effective taunt point was visited.
            if (current.Here == map.Goal && Complete(current.Mask))
            {
                reached = true;
                goalState = current;
                break;
            }

            // Fixed neighbour order (right, left, down, up) makes ties deterministic.
            foreach (Cell next in Exits(map, current))
            {
                if (map.IsWall(next)) continue;

                // Only a walked-in entry shapes a passage cell's future exits; every
                // exit is a 4-neighbour move, so the edge itself is always a walk.
                var state = new Step(next,
                    Probe is not null && Probe.IsOneWay(next) ? current.Here : next,
                    Mark(current.Mask, next));
                if (!visited.Add(state)) continue;

                previous[state] = current;
                queue.Enqueue(state);
            }

            // A teleport is one move, so it keeps BFS on a uniform edge weight.
            // Walking into a passage commits to the straight exit, so no jump may
            // replace it; an arrival without geometry keeps jumps available.
            if (Probe is null || Committed(current)) continue;

            foreach (SpecialTransition jump in Probe.SpecialTransitions(current.Here))
            {
                if (map.IsWall(jump.Destination)) continue;

                // no geometric entry; a jump landing on a taunt point counts as visited
                var state = new Step(jump.Destination, jump.Destination, Mark(current.Mask, jump.Destination));
                if (!visited.Add(state)) continue;

                previous[state] = current;
                queue.Enqueue(state);
            }
        }

        if (!reached)
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
        Step cursor = goalState;

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
        result.Path = path;
        result.Explored = explored;
        result.Cost = Price(map, path);
        result.TauntRequiredCount = Taunt?.Effective.Count ?? 0;
        result.TauntVisitedCount = Taunt?.CountVisited(goalState.Mask) ?? 0;
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

    private static int Price(GridMap map, List<Cell> path)
    {
        int total = 0;
        for (int i = 1; i < path.Count; i++) total += map.CostAt(path[i]);
        return total;
    }
}
