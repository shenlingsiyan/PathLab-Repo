using System;
using System.Collections.Generic;
using System.Linq;

namespace PathLab.Core;

/// <summary>
/// Chaotic portal facility: N portals, every enabled portal can be the exit of any
/// other enabled portal, and the pathfinder decides which exit to take.
///
/// Portal cells are never walkable. When a portal is placed the cell becomes a wall
/// and its old price is remembered, so removing the portal restores the terrain.
/// A teleport therefore always runs EntryCell -> Portal A -> Portal B -> ExitCell,
/// where both entry and exit are ordinary walkable cells next to their portal.
///
/// The facility only publishes connections (and their fee); it never ranks exits,
/// never writes a pairing and never touches an algorithm's cost model.
/// </summary>
public sealed class PortalFacility
{
    /// <summary>One portal. Ids start at 1 and never repeat inside a facility.</summary>
    public sealed class Portal
    {
        internal Portal(int id, Cell position, int restoreCost, int teleportCost)
        {
            Id = id;
            Position = position;
            RestoreCost = restoreCost;
            TeleportCost = teleportCost;
        }

        public int Id { get; }

        public Cell Position { get; }

        public bool Enabled { get; set; } = true;

        /// <summary>Fee charged once when this portal is used as the entrance.</summary>
        public int TeleportCost { get; internal set; }

        /// <summary>Price the cell had before it became a portal; restored on removal.</summary>
        internal int RestoreCost { get; }

        public override string ToString() => $"#{Id} ({Position.X}, {Position.Y})";
    }

    private readonly List<Portal> _portals = new();
    private readonly Dictionary<Cell, Portal> _byCell = new();
    private readonly Dictionary<int, Cell[]> _near = new();
    private readonly Dictionary<Cell, List<Portal>> _enteredFrom = new();
    private int _nextId = 1;
    private int _teleportCost = 2;
    private int _stamp = int.MinValue;

    /// <summary>Master switch: false makes every transition disappear without deleting portals.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fee shared by all portals; 0 means free teleports.</summary>
    public int TeleportCost
    {
        get => _teleportCost;
        set
        {
            int clamped = Math.Clamp(value, 0, GridMap.MaxCost);
            if (clamped == _teleportCost) return;

            _teleportCost = clamped;
            foreach (Portal portal in _portals) portal.TeleportCost = clamped;
            Invalidate();
        }
    }

    public IReadOnlyList<Portal> Portals => _portals;

    /// <summary>Places a portal and turns its cell into a wall; false with a reason otherwise.</summary>
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
            note = $"传送门超出地图边界：({cell.X}, {cell.Y})";
            return false;
        }

        if (cell == map.Start || cell == map.Goal)
        {
            note = "起点与终点不能放置传送门";
            return false;
        }

        if (PortalAt(cell) is { } existing)
        {
            note = $"该格已是传送门 #{existing.Id}";
            return false;
        }

        Portal portal = new(_nextId++, cell, map.CostAt(cell), _teleportCost);
        _portals.Add(portal);
        map.SetCost(cell, GridMap.Wall); // portal cells are never ordinary walkable cells
        Invalidate();
        return true;
    }

    /// <summary>Removes the portal on this cell and restores the price it had before.</summary>
    public bool RemoveAt(GridMap map, Cell cell)
    {
        Portal? portal = PortalAt(cell);
        if (portal is null) return false;

        _portals.Remove(portal);

        if (map is not null && map.InBounds(cell) && cell != map.Start && cell != map.Goal)
            map.SetCost(cell, portal.RestoreCost);

        Invalidate();
        return true;
    }

    /// <summary>Forgets every portal without touching the map; used when a new map replaces the old one.</summary>
    public void Reset()
    {
        _portals.Clear();
        Invalidate();
    }

    public Portal? PortalAt(Cell cell)
    {
        foreach (Portal portal in _portals)
            if (portal.Position == cell)
                return portal;

        return null;
    }

    public bool IsPortal(Cell cell) => PortalAt(cell) is not null;

    public void SetEnabled(int id, bool enabled)
    {
        foreach (Portal portal in _portals)
        {
            if (portal.Id != id) continue;

            portal.Enabled = enabled;
            Invalidate();
            return;
        }
    }

    /// <summary>Re-closes portal cells after the user painted over them.</summary>
    public void CloseCells(GridMap map)
    {
        if (map is null) return;

        bool changed = false;
        foreach (Portal portal in _portals)
        {
            if (portal.Position == map.Start || portal.Position == map.Goal) continue;
            if (map.IsWall(portal.Position)) continue;

            map.SetCost(portal.Position, GridMap.Wall);
            changed = true;
        }

        if (changed) Invalidate();
    }

    /// <summary>Portal cells, in id order; the canvas draws one badge each.</summary>
    public IReadOnlyList<Cell> Positions()
    {
        var cells = new List<Cell>(_portals.Count);
        foreach (Portal portal in _portals) cells.Add(portal.Position);
        return cells;
    }

    /// <summary>Traversal kind of one cell; <see cref="CellKind.Portal"/> is its own case.</summary>
    public CellKind Kind(GridMap map, Cell cell)
    {
        if (map is null || !map.InBounds(cell)) return CellKind.Blocked;
        if (PortalAt(cell) is not null) return CellKind.Portal;
        return map.IsWall(cell) ? CellKind.Blocked : CellKind.NormalWalkable;
    }

    /// <summary>
    /// Ordinary walkable cells next to one portal. The same set serves as entry cells
    /// (when the portal is used as entrance) and exit cells (when it is the exit);
    /// the role is decided by the move, not by the geometry.
    /// </summary>
    public IReadOnlyList<Cell> NearCells(GridMap map, int portalId)
    {
        if (map is null) return Array.Empty<Cell>();

        Ensure(map);
        return _near.TryGetValue(portalId, out Cell[]? cells) ? cells : Array.Empty<Cell>();
    }

    /// <summary>
    /// Every legal teleport that starts from this cell, one per candidate exit of every
    /// other enabled portal. Ordering is (source id, then exit row-major), so the same
    /// map always offers the same list.
    /// </summary>
    public IReadOnlyList<SpecialTransition> TransitionsFrom(GridMap map, Cell cell)
    {
        if (map is null || !Enabled) return Array.Empty<SpecialTransition>();
        if (!map.InBounds(cell) || map.IsWall(cell) || IsPortal(cell))
            return Array.Empty<SpecialTransition>();

        Ensure(map);

        if (!_enteredFrom.TryGetValue(cell, out List<Portal>? sources))
            return Array.Empty<SpecialTransition>();

        var transitions = new List<SpecialTransition>();

        foreach (Portal source in sources)
        {
            if (!source.Enabled) continue;

            foreach (Portal target in _portals)
            {
                if (target.Id == source.Id || !target.Enabled) continue;
                if (!_near.TryGetValue(target.Id, out Cell[]? exits)) continue;

                foreach (Cell exit in exits)
                {
                    // A jump that lands on the cell it left from moves nothing; drop it.
                    if (exit == cell) continue;

                    transitions.Add(new SpecialTransition(
                        exit, TransitionType.Teleport, source.TeleportCost, source.Id, target.Id));
                }
            }
        }

        return transitions;
    }

    /// <summary>True when at least two enabled portals have landable neighbours.</summary>
    public bool HasUsableTransitions(GridMap map)
    {
        if (map is null || !Enabled) return false;

        Ensure(map);

        int usable = 0;
        foreach (Portal portal in _portals)
        {
            if (!portal.Enabled) continue;
            if (_near.TryGetValue(portal.Id, out Cell[]? cells) && cells.Length > 0) usable++;
            if (usable >= 2) return true;
        }

        return false;
    }

    /// <summary>Probe handed to every transition-aware path module.</summary>
    public IPathProbe CreateProbe(GridMap map) => new Probe(this, map);

    /// <summary>Steps of a path that are not 4-neighbour moves, i.e. teleports actually used.</summary>
    public static int CountTeleports(IReadOnlyList<Cell>? path)
    {
        if (path is null || path.Count < 2) return 0;

        int count = 0;
        for (int index = 1; index < path.Count; index++)
        {
            if (Math.Abs(path[index].X - path[index - 1].X)
                + Math.Abs(path[index].Y - path[index - 1].Y) != 1)
                count++;
        }

        return count;
    }

    /// <summary>Drops the cached neighbourhoods; the next query rebuilds them from the map.</summary>
    public void Invalidate() => _stamp = int.MinValue;

    /// <summary>
    /// Rebuilds entry/exit caches only when the portal set or the four cells around each
    /// portal changed. The stamp costs one pass over the portals, never a map scan.
    /// </summary>
    private void Ensure(GridMap map)
    {
        int stamp = Stamp(map);
        if (stamp == _stamp) return;

        _stamp = stamp;
        _near.Clear();
        _enteredFrom.Clear();
        _byCell.Clear();

        foreach (Portal portal in _portals) _byCell[portal.Position] = portal;

        foreach (Portal portal in _portals)
        {
            var cells = new List<Cell>(4);
            foreach (Cell next in map.Neighbours(portal.Position))
                if (!map.IsWall(next) && !_byCell.ContainsKey(next))
                    cells.Add(next);

            Cell[] sorted = cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
            _near[portal.Id] = sorted;

            foreach (Cell cell in sorted)
            {
                if (!_enteredFrom.TryGetValue(cell, out List<Portal>? sources))
                    _enteredFrom[cell] = sources = new List<Portal>();

                sources.Add(portal);
            }
        }
    }

    private int Stamp(GridMap map)
    {
        var hash = new HashCode();
        hash.Add(map.Width);
        hash.Add(map.Height);
        hash.Add(Enabled ? 1 : 0);
        hash.Add(_teleportCost);

        foreach (Portal portal in _portals)
        {
            hash.Add(portal.Id);
            hash.Add(portal.Position.X);
            hash.Add(portal.Position.Y);
            hash.Add(portal.Enabled ? 1 : 0);
            hash.Add(portal.TeleportCost);

            // Only the four cells around a portal can change its entry/exit set.
            foreach (Cell next in map.Neighbours(portal.Position)) hash.Add(map.CostAt(next));
        }

        return hash.ToHashCode();
    }

    private sealed class Probe : IPathProbe
    {
        private readonly PortalFacility _facility;
        private readonly GridMap _map;

        public Probe(PortalFacility facility, GridMap map)
        {
            _facility = facility;
            _map = map;
        }

        public IEnumerable<Cell> NormalNeighbours(Cell cell)
        {
            foreach (Cell next in _map.Neighbours(cell))
                if (!_map.IsWall(next) && !_facility.IsPortal(next))
                    yield return next;
        }

        public IEnumerable<SpecialTransition> SpecialTransitions(Cell cell)
            => _facility.TransitionsFrom(_map, cell);

        public IPathProbe For(GridMap map) => _facility.CreateProbe(map);
    }
}
