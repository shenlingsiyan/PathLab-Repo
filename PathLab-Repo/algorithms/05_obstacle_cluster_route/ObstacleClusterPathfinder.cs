using System;
using System.Collections.Generic;
using PathLab.Core;

namespace PathLab.Algorithms.ObstacleCluster05;

/// <summary>
/// Constrained shortest route: the walk from the spawn to the entrance must touch at least
/// one walkable cell 4-adjacent to the obstacle cluster that owns the reference point.
/// 1. the cluster is the 4-neighbour run of walls around the reference (walls only);
/// 2. the ring is every walkable 4-neighbour of the cluster, deduplicated;
/// 3. two weighted Dijkstra passes (from the spawn and toward the entrance) give the cost
///    of every search state on both legs, so each ring cell is scored without a third search;
/// 4. the cheapest reachable ring cell wins, ties prefer fewer moves then row order.
///
/// One-way passages split the search state into (cell, entry): a walk-in entry must
/// leave straight through, so a ring passage pairs one forward arrival with the one
/// backward departure the arrow allows, while a jump arrival pairs with any exit
/// except the tail side. Plain cells keep a single state and join as before.
///
/// Wall clusters and prices are read from the map only; nothing is written back.
/// </summary>
public sealed class ObstacleClusterPathfinder : IPathfinder, IReferencePointModule, IViaPointModule, ITransitionAware
{
    public string Id => "05-obstacle-cluster-route";

    public string Name => "障碍群周边最短路径";

    public string Summary => "参考点所在障碍群四周必须经过一格；在满足约束的路线中取总代价最低。";

    public SearchObjective Objective => SearchObjective.Cost;

    public bool Optimal => true;

    public bool Constrained => true;

    private Cell? _reference;
    private Cell? _used;
    private Cell? _via;
    private IReadOnlyList<Cell> _ring = Array.Empty<Cell>();

    public Cell? ReferencePoint
    {
        get => _reference;
        set => _reference = value;
    }

    public Cell? UsedReference => _used;

    /// <summary>Optional map facilities (portals); both legs may jump, the ring stays a normal cell set.</summary>
    public IPathProbe? Probe { get; set; }

    /// <summary>Not used by this module; the carrier draws <see cref="ViaCell"/> instead.</summary>
    public Cell? FarthestPoint => null;

    public IReadOnlyList<Cell> RingCells => _ring;

    public Cell? ViaCell => _via;

    /// <summary>One search state: the cell plus where the walker entered it from.</summary>
    private readonly record struct Step(Cell Here, Cell From);

    public SearchResult Search(GridMap map)
    {
        _used = null;
        _via = null;
        _ring = Array.Empty<Cell>();

        var result = new SearchResult();

        if (map is null) return Fail(result, "地图为空");

        if (_reference is not { } reference || !map.InBounds(reference))
            return Fail(result, "参考点超出地图边界");

        if (!map.IsWall(reference)) return Fail(result, "参考点不是障碍");

        _used = reference;

        // GridMap keeps start and goal walkable, but a caller may still hand over a bad map.
        if (!map.InBounds(map.Start) || !map.InBounds(map.Goal)
            || map.IsWall(map.Start) || map.IsWall(map.Goal))
            return Fail(result, "出怪口或入怪口不可通行");

        List<Cell> cluster = ObstacleCluster.Collect(map, reference);
        List<Cell> ring = ObstacleCluster.Ring(map, cluster);
        _ring = ring;

        if (ring.Count == 0) return Fail(result, "障碍群周围没有可通行格子");

        // One pass per leg: forward cost charges each entered cell, backward cost starts at
        // the entrance price because the entrance is also entered on the way in.
        Field forward = Run(map, map.Start, 0);
        Field backward = Run(map, map.Goal, map.CostAt(map.Goal));

        // Join every forward arrival on the ring with every backward departure the
        // passage rules allow, keeping the cheapest (then fewest steps) join per cell.
        var ringSet = new HashSet<Cell>(ring);
        var joins = new Dictionary<Cell, (int Total, int Steps, Step Fwd, Step Back)>();

        foreach (KeyValuePair<Step, int> arrival in forward.StateCost)
        {
            Step fwd = arrival.Key;
            Cell cell = fwd.Here;
            if (!ringSet.Contains(cell)) continue;

            int enterPrice = map.CostAt(cell);

            if (Probe is null || !Probe.IsOneWay(cell))
            {
                // A plain cell holds a single state; every arrival joins with it.
                int outCost = backward.Cost[cell.X, cell.Y];
                if (outCost < 0) continue;

                Consider(cell, arrival.Value, fwd, outCost,
                    backward.Steps[cell.X, cell.Y], new Step(cell, cell), enterPrice);
            }
            else
            {
                foreach (Cell exit in Probe.ExitsAfter(fwd.From, cell))
                {
                    var back = new Step(cell, exit);
                    if (!backward.StateCost.TryGetValue(back, out int outCost)) continue;

                    Consider(cell, arrival.Value, fwd, outCost,
                        backward.StateSteps[back], back, enterPrice);
                }

                // A jump arrival carries no geometry, so it may leave by jump too.
                if (fwd.From == cell
                    && backward.StateCost.TryGetValue(new Step(cell, cell), out int jumpCost))
                    Consider(cell, arrival.Value, fwd, jumpCost,
                        backward.StateSteps[new Step(cell, cell)], new Step(cell, cell), enterPrice);
            }
        }

        void Consider(Cell cell, int inCost, Step fwd, int outCost, int outSteps, Step back, int price)
        {
            int total = inCost + outCost - price;
            int steps = forward.StateSteps[fwd] + outSteps - 1;

            if (joins.TryGetValue(cell, out (int Total, int Steps, Step Fwd, Step Back) incumbent)
                && (incumbent.Total, incumbent.Steps).CompareTo((total, steps)) <= 0)
                return;

            joins[cell] = (total, steps, fwd, back);
        }

        Cell best = default;
        int bestTotal = int.MaxValue;
        int bestSteps = int.MaxValue;

        foreach (Cell target in ring)
        {
            if (!joins.TryGetValue(target, out (int Total, int Steps, Step Fwd, Step Back) join)) continue;

            if (join.Total > bestTotal) continue;
            if (join.Total == bestTotal && join.Steps >= bestSteps) continue;

            bestTotal = join.Total;
            bestSteps = join.Steps;
            best = target;
        }

        if (bestTotal == int.MaxValue) return Fail(result, "周边格子无法同时连接出怪口与入怪口");

        (int _, int _, Step fwdJoin, Step backJoin) = joins[best];
        List<Cell> path = BuildPath(forward, backward, fwdJoin, backJoin, map.Start, map.Goal);
        _via = best;

        result.Found = true;
        result.Cost = SumPathCost(map, path);
        result.Path = path;
        result.Explored = forward.Explored.Count == 0
            ? backward.Explored
            : Concat(forward.Explored, backward.Explored);
        return result;
    }

    private static SearchResult Fail(SearchResult result, string note)
    {
        result.Found = false;
        result.Note = note;
        return result;
    }

    private static List<Cell> Concat(List<Cell> first, List<Cell> second)
    {
        var all = new List<Cell>(first.Count + second.Count);
        all.AddRange(first);
        all.AddRange(second);
        return all;
    }

    /// <summary>
    /// Price of the stitched route. A portal jump is priced the way 01 prices it:
    /// the portal fee plus the terrain price of the cell it lands on.
    /// </summary>
    private int SumPathCost(GridMap map, List<Cell> path)
    {
        int total = 0;

        for (int index = 1; index < path.Count; index++)
        {
            Cell from = path[index - 1];
            Cell to = path[index];
            total += map.CostAt(to);

            if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) == 1) continue;

            total += JumpFee(from, to);
        }

        return total;
    }

    /// <summary>Fee charged for this jump; 0 when the path has no jumps or no probe is attached.</summary>
    private int JumpFee(Cell from, Cell to)
    {
        if (Probe is null) return 0;

        int fee = int.MaxValue;
        foreach (SpecialTransition jump in Probe.SpecialTransitions(from))
            if (jump.Destination == to && jump.Cost < fee)
                fee = jump.Cost;

        return fee == int.MaxValue ? 0 : fee;
    }

    /// <summary>
    /// Stitches the two legs at the ring cell. The forward chain ends on the ring cell,
    /// the backward chain continues from it, so every step stays 4-adjacent and the
    /// straight-through rule of a ring passage survives the join.
    /// </summary>
    private static List<Cell> BuildPath(Field forward, Field backward, Step fwd, Step back,
        Cell start, Cell goal)
    {
        var head = new List<Cell>();
        Step cursor = fwd;

        while (true)
        {
            head.Add(cursor.Here);
            if (cursor.Here == start) break;

            cursor = forward.StatePrev[cursor];
        }

        head.Reverse();

        var tail = new List<Cell>();

        if (back.Here != goal)
        {
            cursor = back;

            while (cursor.Here != goal)
            {
                if (cursor.Here != back.Here) tail.Add(cursor.Here);
                cursor = backward.StatePrev[cursor];
            }

            tail.Add(goal);
        }

        head.AddRange(tail);
        return head;
    }

    /// <summary>
    /// Weighted Dijkstra over (cell, entry) states with predecessors and step counts.
    /// The relaxation is identical for both legs: entering the next cell costs its price,
    /// a facility jump costs the portal fee plus the price of the cell it lands on.
    /// Tie-break is (cost, steps, y, x) so equal-cost routes prefer fewer moves.
    /// </summary>
    private Field Run(GridMap map, Cell origin, int originCost)
    {
        var field = new Field
        {
            Cost = NewField(map.Width, map.Height, -1),
            Steps = NewField(map.Width, map.Height, -1)
        };

        var queue = new PriorityQueue<Step, (int Cost, int Steps, int Y, int X)>();
        var origin_ = new Step(origin, origin);
        field.StateCost[origin_] = originCost;
        field.StateSteps[origin_] = 0;
        field.Cost[origin.X, origin.Y] = originCost;
        field.Steps[origin.X, origin.Y] = 0;
        queue.Enqueue(origin_, (originCost, 0, origin.Y, origin.X));

        while (queue.TryDequeue(out Step current, out (int Cost, int Steps, int Y, int X) priority))
        {
            if (priority.Cost > field.StateCost[current]) continue;

            if (field.Seen.Add(current.Here)) field.Explored.Add(current.Here);

            foreach (Cell next in GeometricExits(map, current))
            {
                int step = map.CostAt(next);
                if (step == GridMap.Wall) continue;

                // Only a walked-in entry shapes a passage cell's future exits; every
                // exit is a 4-neighbour move, so the edge itself is always a walk.
                Relax(field, queue, current,
                    new Step(next, Probe is not null && Probe.IsOneWay(next) ? current.Here : next),
                    priority, step);
            }

            // Walking into a passage commits to the straight exit, so no jump may
            // replace it; an arrival without geometry keeps jumps available.
            if (Probe is null || Committed(current)) continue;

            foreach (SpecialTransition jump in Probe.SpecialTransitions(current.Here))
            {
                int step = map.CostAt(jump.Destination);
                if (step == GridMap.Wall) continue;

                Relax(field, queue, current,
                    new Step(jump.Destination, jump.Destination), priority, jump.Cost + step);
            }
        }

        return field;
    }

    private static void Relax(Field field, PriorityQueue<Step, (int Cost, int Steps, int Y, int X)> queue,
        Step from, Step state, (int Cost, int Steps, int Y, int X) priority, int step)
    {
        int candidate = priority.Cost + step;
        if (field.StateCost.TryGetValue(state, out int known) && known <= candidate) return;

        field.StateCost[state] = candidate;
        field.StateSteps[state] = priority.Steps + 1;
        field.StatePrev[state] = from;

        int cellCost = field.Cost[state.Here.X, state.Here.Y];
        if (cellCost < 0 || candidate < cellCost)
        {
            field.Cost[state.Here.X, state.Here.Y] = candidate;
            field.Steps[state.Here.X, state.Here.Y] = priority.Steps + 1;
        }

        queue.Enqueue(state, (candidate, priority.Steps + 1, state.Here.Y, state.Here.X));
    }

    /// <summary>True when the state walked into a passage cell, so only its straight exit remains.</summary>
    private bool Committed(Step state)
        => Probe is not null && Probe.IsOneWay(state.Here)
           && Math.Abs(state.From.X - state.Here.X) + Math.Abs(state.From.Y - state.Here.Y) == 1;

    /// <summary>Entry-aware exits: plain walkable neighbours without a probe, facility exits with one.</summary>
    private IEnumerable<Cell> GeometricExits(GridMap map, Step state)
        => Probe is null ? map.Neighbours(state.Here) : Probe.ExitsAfter(state.From, state.Here);

    private static int[,] NewField(int width, int height, int value)
    {
        var array = new int[width, height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            array[x, y] = value;

        return array;
    }

    private sealed class Field
    {
        public int[,] Cost = new int[0, 0];
        public int[,] Steps = new int[0, 0];
        public List<Cell> Explored = new();
        public HashSet<Cell> Seen = new();
        public Dictionary<Step, int> StateCost = new();
        public Dictionary<Step, int> StateSteps = new();
        public Dictionary<Step, Step> StatePrev = new();
    }
}
