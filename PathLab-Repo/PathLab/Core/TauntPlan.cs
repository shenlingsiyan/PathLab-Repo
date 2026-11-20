using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// The effective taunt set of one search plus the bit mapping the search states use.
///
/// T' = { enabled point | some legal start-to-goal route passes through it }.
/// "Legal" means the whole probe: ordinary 4-neighbour moves, one-way exits,
/// portal jumps and launcher chains are all part of the reachability check.
///
/// The check is done on the same (cell, entry) states the search itself uses:
/// a point is effective when at least one of its states is reachable from the
/// start and can still reach the goal. Points that are walled in, cut off, or
/// reachable only through a dead end are therefore never forced.
///
/// Points are ordered row-major, so the same map always assigns the same bits.
/// The plan never runs the search: it only decides which points are mandatory.
/// </summary>
public sealed class TauntPlan
{
    /// <summary>One search state: the cell plus where the walker entered it from.</summary>
    private readonly record struct Node(Cell Here, Cell From);

    /// <summary>A plan with no effective point: every search keeps its plain behaviour.</summary>
    public static readonly TauntPlan None = new(Array.Empty<TauntPoint>());

    private readonly Dictionary<Cell, int> _bits;

    private TauntPlan(IReadOnlyList<TauntPoint> effective)
    {
        Effective = effective;
        _bits = new Dictionary<Cell, int>(effective.Count);

        for (int index = 0; index < effective.Count; index++)
            _bits[effective[index].Position] = index;

        // 0 points -> 0; 1 point -> 1; 2 points -> 3; ...
        FullMask = effective.Count == 0 ? 0 : (1 << effective.Count) - 1;
    }

    /// <summary>Points this search must pass through, row-major.</summary>
    public IReadOnlyList<TauntPoint> Effective { get; }

    /// <summary>Mask with every effective point marked.</summary>
    public int FullMask { get; }

    public bool HasAny => Effective.Count > 0;

    /// <summary>Bit of this cell, or -1 when the cell is not an effective taunt point.</summary>
    public int BitOf(Cell cell)
        => _bits.TryGetValue(cell, out int bit) ? bit : -1;

    /// <summary>Marks the point on <paramref name="cell"/> when there is one; otherwise the mask is unchanged.</summary>
    public int Mark(int mask, Cell cell)
    {
        if (_bits.Count == 0) return mask;

        return _bits.TryGetValue(cell, out int bit) ? mask | 1 << bit : mask;
    }

    /// <summary>True when every effective point has been visited.</summary>
    public bool Complete(int mask) => mask == FullMask;

    /// <summary>Effective points still missing from this mask, row-major.</summary>
    public IReadOnlyList<TauntPoint> Missing(int mask)
    {
        var missing = new List<TauntPoint>();
        for (int index = 0; index < Effective.Count; index++)
            if ((mask & 1 << index) == 0)
                missing.Add(Effective[index]);

        return missing;
    }

    public int CountVisited(int mask)
    {
        int count = 0;
        for (int value = mask; value != 0; value &= value - 1) count++;
        return count;
    }

    /// <summary>
    /// Builds the effective set for one start-to-goal search. Enabled points that are
    /// outside the map, walled in, unreachable, or unable to reach the goal are skipped.
    /// </summary>
    public static TauntPlan Build(GridMap? map, IPathProbe? probe, IReadOnlyList<TauntPoint>? points)
    {
        if (map is null || points is null || points.Count == 0) return None;

        var candidates = new List<TauntPoint>();

        foreach (TauntPoint point in points)
        {
            if (!point.Enabled) continue;
            if (!map.InBounds(point.Position)) continue;
            if (map.IsWall(point.Position)) continue;

            candidates.Add(point);
        }

        if (candidates.Count == 0) return None;

        // Points that share a cell share one bit set: they are reached together.
        candidates.Sort((left, right) =>
        {
            int row = left.Position.Y.CompareTo(right.Position.Y);
            return row != 0 ? row : left.Position.X.CompareTo(right.Position.X);
        });

        IPathProbe walkProbe = probe ?? OneWayFacility.Plain(map);

        var graph = BuildGraph(map, walkProbe);
        var statesByCell = new Dictionary<Cell, List<Node>>();

        foreach (Node node in graph.Keys)
        {
            if (!statesByCell.TryGetValue(node.Here, out List<Node>? list))
                statesByCell[node.Here] = list = new List<Node>();

            list.Add(node);
        }

        HashSet<Node> forward = Reachable(graph, new Node(map.Start, map.Start));

        var reverse = new Dictionary<Node, List<Node>>();
        foreach (KeyValuePair<Node, List<Node>> edge in graph)
        {
            if (!reverse.ContainsKey(edge.Key)) reverse[edge.Key] = new List<Node>();

            foreach (Node next in edge.Value)
            {
                if (!reverse.TryGetValue(next, out List<Node>? list))
                    reverse[next] = list = new List<Node>();

                list.Add(edge.Key);
            }
        }

        var backward = new HashSet<Node>();
        var queue = new Queue<Node>();

        foreach (Node node in graph.Keys)
            if (node.Here == map.Goal && backward.Add(node))
                queue.Enqueue(node);

        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();
            if (!reverse.TryGetValue(current, out List<Node>? predecessors)) continue;

            foreach (Node predecessor in predecessors)
                if (backward.Add(predecessor))
                    queue.Enqueue(predecessor);
        }

        var effective = new List<TauntPoint>();

        foreach (TauntPoint point in candidates)
        {
            if (!statesByCell.TryGetValue(point.Position, out List<Node>? states)) continue;

            foreach (Node state in states)
            {
                if (!forward.Contains(state) || !backward.Contains(state)) continue;

                effective.Add(point);
                break;
            }
        }

        return effective.Count == 0 ? None : new TauntPlan(effective);
    }

    /// <summary>
    /// Every state of the map and its legal successors, built from the probe alone:
    /// geometric exits honour one-way entries, and facility jumps are added when the
    /// walker is not committed to a straight passage exit.
    /// </summary>
    private static Dictionary<Node, List<Node>> BuildGraph(GridMap map, IPathProbe probe)
    {
        var graph = new Dictionary<Node, List<Node>>();

        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            var cell = new Cell(x, y);
            if (map.IsWall(cell)) continue;

            // A plain cell holds one state; a passage cell holds one per possible entry.
            var entries = new List<Cell>(5) { cell };

            if (probe.IsOneWay(cell))
            {
                foreach (Cell side in map.Neighbours(cell))
                    if (!map.IsWall(side))
                        entries.Add(side);
            }

            foreach (Cell entry in entries)
            {
                var node = new Node(cell, entry);
                var successors = new List<Node>(8);

                foreach (Cell next in probe.ExitsAfter(entry, cell))
                {
                    if (map.IsWall(next)) continue;
                    successors.Add(new Node(next, probe.IsOneWay(next) ? cell : next));
                }

                // Walking into a passage commits to its straight exit, so no jump may replace it.
                bool committed = probe.IsOneWay(cell)
                                 && Math.Abs(entry.X - cell.X) + Math.Abs(entry.Y - cell.Y) == 1;

                if (!committed)
                    foreach (SpecialTransition jump in probe.SpecialTransitions(cell))
                        if (!map.IsWall(jump.Destination))
                            successors.Add(new Node(jump.Destination, jump.Destination));

                graph[node] = successors;
            }
        }

        return graph;
    }

    private static HashSet<Node> Reachable(Dictionary<Node, List<Node>> graph, Node origin)
    {
        var reached = new HashSet<Node>();
        if (!graph.ContainsKey(origin)) return reached;

        reached.Add(origin);
        var queue = new Queue<Node>();
        queue.Enqueue(origin);

        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();
            if (!graph.TryGetValue(current, out List<Node>? successors)) continue;

            foreach (Node next in successors)
                if (reached.Add(next))
                    queue.Enqueue(next);
        }

        return reached;
    }
}
