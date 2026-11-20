using System;
using System.Collections.Generic;
using System.Linq;

namespace PathLab.Core;

/// <summary>The four cardinal directions a one-way arrow can point to.</summary>
public enum Direction
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>One grid step of a direction; Y grows downward, so Up is (0,-1).</summary>
public static class Directions
{
    public static Cell Step(Direction direction) => direction switch
    {
        Direction.Up => new Cell(0, -1),
        Direction.Down => new Cell(0, 1),
        Direction.Left => new Cell(-1, 0),
        _ => new Cell(1, 0)
    };
}

/// <summary>One one-way passage cell: the arrow points from its tail to its head.</summary>
public readonly record struct OneWayPassage(Cell Position, Direction Direction);

/// <summary>
/// One-way passage facility: every passage occupies one still-walkable cell and
/// forbids exactly two edges around it — entering from the head side and leaving
/// toward the tail side. Crossing the cell is a straight line: a walk-in entry may
/// only leave through the opposite side, so side moves exist in the edge pool but
/// can never be combined with a turn inside the passage. An arrival without
/// geometry (a teleport landing) may leave anywhere except the tail side.
///
/// The facility never paints the map: the passage cell keeps its terrain price,
/// boundaries and obstacles keep their usual rules, and a missing head or tail
/// neighbour simply removes the corresponding edge instead of the passage.
/// </summary>
public sealed class OneWayFacility
{
    private readonly List<OneWayPassage> _passages = new();
    private readonly Dictionary<Cell, Direction> _byCell = new();

    public IReadOnlyList<OneWayPassage> Passages => _passages;

    /// <summary>Places a passage on a walkable cell; false with a reason otherwise.</summary>
    public bool Add(GridMap map, Cell cell, Direction direction, out string? note)
    {
        note = null;

        if (map is null)
        {
            note = "地图为空";
            return false;
        }

        if (!map.InBounds(cell))
        {
            note = $"单向通道超出地图边界：({cell.X}, {cell.Y})";
            return false;
        }

        if (cell == map.Start || cell == map.Goal)
        {
            note = "起点与终点不能放置单向通道";
            return false;
        }

        if (_byCell.ContainsKey(cell))
        {
            note = "该格已有单向通道";
            return false;
        }

        _passages.Add(new OneWayPassage(cell, direction));
        _byCell[cell] = direction;
        return true;
    }

    /// <summary>Removes the passage on this cell; the map needs no repair.</summary>
    public bool RemoveAt(Cell cell)
    {
        if (!_byCell.Remove(cell)) return false;

        _passages.RemoveAll(passage => passage.Position == cell);
        return true;
    }

    /// <summary>Forgets every passage; used when a new map replaces the old one.</summary>
    public void Reset()
    {
        _passages.Clear();
        _byCell.Clear();
    }

    public OneWayPassage? At(Cell cell)
        => _byCell.TryGetValue(cell, out Direction direction)
            ? new OneWayPassage(cell, direction)
            : null;

    /// <summary>True when this exact edge is one of the two forbidden edges.</summary>
    public static bool Forbids(OneWayPassage passage, Cell from, Cell to)
    {
        Cell step = Directions.Step(passage.Direction);
        Cell head = new(passage.Position.X + step.X, passage.Position.Y + step.Y);
        Cell tail = new(passage.Position.X - step.X, passage.Position.Y - step.Y);

        return (from == head && to == passage.Position)   // entering from the head side
               || (from == passage.Position && to == tail); // leaving toward the tail side
    }

    /// <summary>
    /// True when the move from one cell to its neighbour violates any passage.
    /// Both ends are checked: the move may leave a passage or enter one.
    /// </summary>
    public bool BlocksMove(Cell from, Cell to)
    {
        if (_byCell.Count == 0) return false;

        if (_byCell.TryGetValue(to, out Direction into)
            && Forbids(new OneWayPassage(to, into), from, to)) return true;

        if (_byCell.TryGetValue(from, out Direction outOf)
            && Forbids(new OneWayPassage(from, outOf), from, to)) return true;

        return false;
    }

    /// <summary>Passage cells, in placement order; the canvas draws one arrow each.</summary>
    public IReadOnlyList<Cell> Positions()
        => _passages.Select(passage => passage.Position).ToList();

    /// <summary>
    /// Wraps another probe and filters its normal moves through the one-way rules.
    /// Special (non-geometric) transitions pass through untouched: a teleport is
    /// not a physical edge, only the walk away from its landing is constrained.
    /// </summary>
    public IPathProbe ApplyTo(IPathProbe inner) => new FilteredProbe(inner, this);

    /// <summary>A facility-free probe: plain walkable 4-neighbours, no special moves.</summary>
    public static IPathProbe Plain(GridMap map) => new PlainProbe(map);

    private sealed class PlainProbe : IPathProbe
    {
        private readonly GridMap _map;

        public PlainProbe(GridMap map) => _map = map;

        public IEnumerable<Cell> NormalNeighbours(Cell cell)
        {
            foreach (Cell next in _map.Neighbours(cell))
                if (!_map.IsWall(next))
                    yield return next;
        }

        public IEnumerable<SpecialTransition> SpecialTransitions(Cell cell)
            => Array.Empty<SpecialTransition>();

        public IPathProbe For(GridMap map) => new PlainProbe(map);
    }

    private sealed class FilteredProbe : IPathProbe
    {
        private readonly IPathProbe _inner;
        private readonly OneWayFacility _facility;

        public FilteredProbe(IPathProbe inner, OneWayFacility facility)
        {
            _inner = inner;
            _facility = facility;
        }

        public IEnumerable<Cell> NormalNeighbours(Cell cell)
        {
            foreach (Cell next in _inner.NormalNeighbours(cell))
                if (!_facility.BlocksMove(cell, next))
                    yield return next;
        }

        public IEnumerable<SpecialTransition> SpecialTransitions(Cell cell)
            => _inner.SpecialTransitions(cell);

        public bool IsOneWay(Cell cell) => _facility.At(cell) is not null;

        public IEnumerable<Cell> ExitsAfter(Cell entry, Cell cell)
        {
            if (_facility.At(cell) is not { } passage)
            {
                foreach (Cell next in _inner.NormalNeighbours(cell))
                    if (!_facility.BlocksMove(cell, next))
                        yield return next;

                yield break;
            }

            // A walked-in entry leaves straight through; any other arrival (teleport
            // landing, search origin) keeps every exit except the tail side.
            bool walkedIn = Math.Abs(entry.X - cell.X) + Math.Abs(entry.Y - cell.Y) == 1;

            Cell straight = new(2 * cell.X - entry.X, 2 * cell.Y - entry.Y);
            Cell step = Directions.Step(passage.Direction);
            Cell tail = new(cell.X - step.X, cell.Y - step.Y);

            foreach (Cell next in _inner.NormalNeighbours(cell))
            {
                if (_facility.BlocksMove(cell, next)) continue;
                if (walkedIn ? next != straight : next == tail) continue;

                yield return next;
            }
        }

        public IPathProbe For(GridMap map) => new FilteredProbe(_inner.For(map), _facility);
    }
}
