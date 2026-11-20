using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// Contract for analysis modules that answer "which cell is farthest from a reference cell",
/// as opposed to <see cref="IPathfinder"/>, which answers "how do I walk from start to goal".
/// A module is one folder under /algorithms; the carrier compiles a copy of it.
/// </summary>
public interface IFarthestPointFinder
{
    /// <summary>Stable sort key, for example "02-farthest-from-target".</summary>
    string Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>One-line explanation.</summary>
    string Summary { get; }

    /// <summary>
    /// Distance is the fewest moves from <paramref name="target"/> to a cell,
    /// 4-neighbour, terrain prices ignored (only walls block).
    /// </summary>
    FarthestPointResult Find(GridMap map, Cell target);
}

/// <summary>Outcome of one farthest-point search.</summary>
public sealed class FarthestPointResult
{
    /// <summary>Distance value of a cell that cannot be reached from the reference cell.</summary>
    public const int Unreachable = -1;

    public bool Found { get; set; }

    /// <summary>Chosen farthest cell; only meaningful when <see cref="Found"/>.</summary>
    public Cell Cell { get; set; }

    /// <summary>Moves from the reference cell to <see cref="Cell"/>.</summary>
    public int Distance { get; set; }

    /// <summary>Every cell on the farthest layer, ordered by the module's tie-break rule.</summary>
    public List<Cell> Candidates { get; set; } = new();

    /// <summary>Move distances from the reference cell, indexed [x, y]; unvisited stays <see cref="Unreachable"/>.</summary>
    public int[,] Distances { get; set; } = new int[0, 0];

    public string? Note { get; set; }

    /// <summary>Bounds-safe lookup into <see cref="Distances"/>.</summary>
    public int DistanceAt(Cell cell)
        => cell.X >= 0 && cell.Y >= 0 && cell.X < Distances.GetLength(0) && cell.Y < Distances.GetLength(1)
            ? Distances[cell.X, cell.Y]
            : Unreachable;
}
