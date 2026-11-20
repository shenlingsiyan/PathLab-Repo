using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// One single-use taunt point: a walkable marker the next route must pass through
/// once. It never blocks, never changes a price and never moves the walker by itself;
/// it only adds a "must visit" requirement to the search.
/// </summary>
public sealed class TauntPoint
{
    internal TauntPoint(int id, Cell position)
    {
        Id = id;
        Position = position;
    }

    /// <summary>Facility-unique id, starting at 1 and never reused.</summary>
    public int Id { get; }

    public Cell Position { get; }

    public bool Enabled { get; internal set; } = true;

    public override string ToString() => $"#{Id} ({Position.X}, {Position.Y})";
}

/// <summary>
/// Taunt point facility. Points do not paint the map: the cell keeps its terrain,
/// its price and all of its ordinary 4-neighbour edges. Their only effect is that
/// the pathfinder is asked to pass through every point that can still lie on a
/// legal start-to-goal route (see <see cref="TauntPlan"/>).
///
/// "Single use" is per search: visiting a point once satisfies it for that route.
/// The point stays on the map for every later search and for every other robot.
/// </summary>
public sealed class TauntPointFacility
{
    private readonly List<TauntPoint> _points = new();
    private readonly Dictionary<Cell, TauntPoint> _byCell = new();
    private int _nextId = 1;

    public IReadOnlyList<TauntPoint> Points => _points;

    /// <summary>Places a taunt point on an in-bounds cell; the terrain is untouched.</summary>
    public bool Add(GridMap map, Cell cell, out string? note)
    {
        note = null;

        if (map is null)
        {
            note = "地图为空";
            return false;
        }

        if (!map.InBounds(cell))
        {
            note = $"嘲讽点超出地图边界：({cell.X}, {cell.Y})";
            return false;
        }

        if (cell == map.Start || cell == map.Goal)
        {
            note = "起点与终点不能放置嘲讽点";
            return false;
        }

        if (_byCell.ContainsKey(cell))
        {
            note = "该格已有嘲讽点";
            return false;
        }

        TauntPoint point = new(_nextId++, cell);
        _points.Add(point);
        _byCell[cell] = point;
        return true;
    }

    /// <summary>Removes the point on this cell; the map needs no repair.</summary>
    public bool RemoveAt(Cell cell)
    {
        if (!_byCell.TryGetValue(cell, out TauntPoint? point)) return false;

        _byCell.Remove(cell);
        _points.Remove(point);
        return true;
    }

    /// <summary>Forgets every point; used when a new map replaces the old one.</summary>
    public void Reset()
    {
        _points.Clear();
        _byCell.Clear();
    }

    public TauntPoint? At(Cell cell)
        => _byCell.TryGetValue(cell, out TauntPoint? point) ? point : null;

    public bool IsTaunt(Cell cell) => _byCell.ContainsKey(cell);

    public bool SetEnabled(int id, bool enabled)
    {
        foreach (TauntPoint point in _points)
        {
            if (point.Id != id) continue;

            point.Enabled = enabled;
            return true;
        }

        return false;
    }

    /// <summary>Enabled points only, in placement order.</summary>
    public IReadOnlyList<TauntPoint> EnabledPoints()
    {
        var enabled = new List<TauntPoint>();
        foreach (TauntPoint point in _points)
            if (point.Enabled)
                enabled.Add(point);

        return enabled;
    }

    /// <summary>Point cells, in placement order; the canvas draws one red square each.</summary>
    public IReadOnlyList<Cell> Positions()
    {
        var cells = new List<Cell>(_points.Count);
        foreach (TauntPoint point in _points) cells.Add(point.Position);
        return cells;
    }
}
