using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// Pure helpers for "route must touch an obstacle cluster": 4-neighbour wall clusters,
/// their deduplicated walkable ring, and a deterministic default wall.
/// Nothing here writes to the map.
/// </summary>
public static class ObstacleCluster
{
    /// <summary>All wall cells connected to <paramref name="seed"/> through 4-neighbour walls.</summary>
    public static List<Cell> Collect(GridMap map, Cell seed)
    {
        var cluster = new List<Cell>();
        if (map is null || !map.InBounds(seed) || !map.IsWall(seed)) return cluster;

        var seen = new bool[map.Width, map.Height];
        var queue = new Queue<Cell>();
        seen[seed.X, seed.Y] = true;
        queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            Cell current = queue.Dequeue();
            cluster.Add(current);

            foreach (Cell next in map.Neighbours(current))
            {
                if (!map.IsWall(next) || seen[next.X, next.Y]) continue;

                seen[next.X, next.Y] = true;
                queue.Enqueue(next);
            }
        }

        return cluster;
    }

    /// <summary>
    /// Walkable cells 4-adjacent to at least one cluster cell, deduplicated and row-major.
    /// A cell touching several cluster cells still appears once.
    /// </summary>
    public static List<Cell> Ring(GridMap map, IReadOnlyList<Cell> cluster)
    {
        var ring = new List<Cell>();
        if (map is null || cluster.Count == 0) return ring;

        var inCluster = new bool[map.Width, map.Height];
        foreach (Cell cell in cluster)
            if (map.InBounds(cell))
                inCluster[cell.X, cell.Y] = true;

        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            var cell = new Cell(x, y);
            if (map.IsWall(cell)) continue;

            foreach (Cell next in map.Neighbours(cell))
            {
                if (!inCluster[next.X, next.Y]) continue;

                ring.Add(cell);
                break;
            }
        }

        return ring;
    }

    /// <summary>First wall in row-major order, or null when the map has none.</summary>
    public static Cell? FirstWall(GridMap map)
    {
        if (map is null) return null;

        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
            if (map.IsWall(new Cell(x, y)))
                return new Cell(x, y);

        return null;
    }
}
