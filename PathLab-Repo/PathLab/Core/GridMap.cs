using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>Grid coordinate. X is column, Y is row.</summary>
public readonly record struct Cell(int X, int Y);

/// <summary>
/// One map: size, start/goal and per-cell traversal cost.
/// Cost 0 means impassable; cost 1..9 is the price of entering the cell.
/// The start and goal can never become impassable.
/// </summary>
public sealed class GridMap
{
    public const int Wall = 0;
    public const int MinCost = 1;
    public const int MaxCost = 9;

    private readonly int[,] _cost;

    private static readonly (int Dx, int Dy)[] Steps =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1)
    };

    public GridMap(int width, int height)
    {
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(width), "Map must be at least 2x2.");

        Width = width;
        Height = height;
        _cost = new int[width, height];

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            _cost[x, y] = MinCost;

        Start = new Cell(0, 0);
        Goal = new Cell(width - 1, height - 1);
    }

    public int Width { get; }
    public int Height { get; }
    public Cell Start { get; set; }
    public Cell Goal { get; set; }

    public bool InBounds(Cell cell)
        => cell.X >= 0 && cell.Y >= 0 && cell.X < Width && cell.Y < Height;

    public int CostAt(Cell cell)
        => InBounds(cell) ? _cost[cell.X, cell.Y] : Wall;

    public bool IsWall(Cell cell) => CostAt(cell) == Wall;

    /// <summary>Paint one cell. Walls cannot overwrite the start or the goal.</summary>
    public void SetCost(Cell cell, int cost)
    {
        if (!InBounds(cell)) return;

        int clamped = Math.Clamp(cost, Wall, MaxCost);
        if (clamped == Wall && (cell == Start || cell == Goal)) return;

        _cost[cell.X, cell.Y] = clamped;
    }

    /// <summary>Apply one empty-cell (traversable) price to every non-wall cell.</summary>
    public void SetAllTraversableCost(int cost)
    {
        int clamped = Math.Clamp(cost, MinCost, MaxCost);

        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (_cost[x, y] > Wall)
                _cost[x, y] = clamped;
    }

    public IEnumerable<Cell> Neighbours(Cell cell)
    {
        foreach ((int dx, int dy) in Steps)
        {
            var next = new Cell(cell.X + dx, cell.Y + dy);
            if (InBounds(next)) yield return next;
        }
    }

    public GridMap Clone()
    {
        var copy = new GridMap(Width, Height);
        Array.Copy(_cost, copy._cost, _cost.Length);
        copy.Start = Start;
        copy.Goal = Goal;
        return copy;
    }
}
