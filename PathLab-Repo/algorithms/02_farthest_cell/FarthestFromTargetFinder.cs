using System;
using System.Collections.Generic;
using PathLab.Core;

namespace PathLab.Algorithms.FarthestCell02;

/// <summary>
/// Breadth-first search from a given reference cell: returns the reachable cell needing the most moves.
/// Distance is pure move count (4-neighbour, walls block, terrain prices ignored).
/// The reference may be a wall (a gate carved into a wall): the search then starts from its
/// traversable neighbours at distance 1, because stepping out of the gate is the first move.
/// Ties resolve by row-major order (smallest y, then smallest x) so results are deterministic.
/// With a probe, facility jumps count as one move too, so a region reachable only by portal
/// still takes part in the "farthest" comparison.
///
/// One-way passages split the search state: a walk-in entry must leave straight through,
/// so a passage cell holds one distance per entry and reports the smallest. A teleport
/// landing arrives without geometry and may leave anywhere except the tail side.
///
/// This module answers a query, it does not produce a start-to-goal path, so it is not an IPathfinder.
/// </summary>
public sealed class FarthestFromTargetFinder : IFarthestPointFinder, ITransitionAware
{
    public string Id => "02-farthest-from-target";

    public string Name => "最远可达格子 (BFS)";

    public string Summary => "以指定目标点为起点 BFS，返回最短步数最大的可达格子（目标点可为空地或障碍）。";

    /// <summary>Optional map facilities (portals); null keeps the plain 4-neighbour search.</summary>
    public IPathProbe? Probe { get; set; }

    /// <summary>One search state: the cell plus where the walker entered it from.</summary>
    private readonly record struct Step(Cell Here, Cell From);

    public FarthestPointResult Find(GridMap map, Cell target)
    {
        var result = new FarthestPointResult();

        if (map is null)
        {
            result.Note = "地图为空";
            return result;
        }

        if (!map.InBounds(target))
        {
            result.Note = "目标点超出地图边界";
            return result;
        }

        int width = map.Width;
        int height = map.Height;
        int[,] distance = new int[width, height];

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            distance[x, y] = FarthestPointResult.Unreachable;

        var best = new Dictionary<Step, int>();
        var queue = new Queue<Step>();

        // One distance per state; a cell reports the smallest over its entries.
        void Seed(Step state, int value)
        {
            if (!best.TryAdd(state, value)) return;

            ref int cellDistance = ref distance[state.Here.X, state.Here.Y];
            if (cellDistance < 0 || value < cellDistance) cellDistance = value;
            queue.Enqueue(state);
        }

        if (map.IsWall(target))
        {
            // Obstacle reference (a gate carved into a wall, for instance): the walk starts by
            // stepping out of it, so every traversable neighbour sits at distance 1.
            var seeded = false;
            foreach (Cell neighbour in GeometricExits(map, new Step(target, target)))
            {
                if (map.IsWall(neighbour)) continue;

                Seed(new Step(neighbour, target), 1);
                seeded = true;
            }

            if (!seeded)
            {
                result.Note = "参考点四周没有可通行格子";
                return result;
            }
        }
        else
        {
            Seed(new Step(target, target), 0);
        }

        // Every dequeue expands one layer: entry-aware exits, then facility jumps.
        while (queue.Count > 0)
        {
            Step current = queue.Dequeue();
            int next = best[current] + 1;

            foreach (Cell neighbour in GeometricExits(map, current))
            {
                if (map.IsWall(neighbour)) continue;

                // Only a walked-in entry shapes a passage cell's future exits; every
                // exit is a 4-neighbour move, so the edge itself is always a walk.
                Seed(new Step(neighbour,
                        Probe is not null && Probe.IsOneWay(neighbour) ? current.Here : neighbour),
                    next);
            }

            // Walking into a passage commits to the straight exit, so no jump may
            // replace it; an arrival without geometry keeps jumps available.
            if (Probe is null || Committed(current)) continue;

            foreach (SpecialTransition jump in Probe.SpecialTransitions(current.Here))
            {
                if (map.IsWall(jump.Destination)) continue;

                Seed(new Step(jump.Destination, jump.Destination), next); // no geometric entry
            }
        }

        int maxDistance = FarthestPointResult.Unreachable;
        var candidates = new List<Cell>();

        // Row-major scan: candidates[0] is the deterministic farthest point.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int value = distance[x, y];
            if (value == FarthestPointResult.Unreachable) continue;

            var cell = new Cell(x, y);

            if (value > maxDistance)
            {
                maxDistance = value;
                candidates.Clear();
                candidates.Add(cell);
            }
            else if (value == maxDistance)
            {
                candidates.Add(cell);
            }
        }

        result.Distances = distance;

        if (candidates.Count == 0)
        {
            result.Note = "没有可达格子";
            return result;
        }

        result.Found = true;
        result.Cell = candidates[0];
        result.Distance = maxDistance;
        result.Candidates = candidates;
        return result;
    }

    /// <summary>True when the state walked into a passage cell, so only its straight exit remains.</summary>
    private bool Committed(Step state)
        => Probe is not null && Probe.IsOneWay(state.Here)
           && Math.Abs(state.From.X - state.Here.X) + Math.Abs(state.From.Y - state.Here.Y) == 1;

    /// <summary>
    /// Cells reachable from the state's cell by one walking move: entry-aware facility
    /// exits with a probe, plain 4-neighbours without one. Walls are left to the caller.
    /// </summary>
    private IEnumerable<Cell> GeometricExits(GridMap map, Step state)
        => Probe is null ? Walkable(map, state.Here) : Probe.ExitsAfter(state.From, state.Here);

    private static IEnumerable<Cell> Walkable(GridMap map, Cell cell)
    {
        foreach (Cell next in map.Neighbours(cell))
            if (!map.IsWall(next))
                yield return next;
    }
}
