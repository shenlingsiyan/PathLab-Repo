using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// One directional launcher: a still-walkable cell that, when used on purpose,
/// throws the walker along its fixed direction for a positive, editable distance.
/// </summary>
public sealed class DirectionalLauncher
{
    internal DirectionalLauncher(int id, Cell position, Direction direction, int distance)
    {
        Id = id;
        Position = position;
        Direction = direction;
        TransferDistance = distance;
    }

    /// <summary>Facility-unique id, starting at 1 and never reused.</summary>
    public int Id { get; }

    public Cell Position { get; }

    /// <summary>Fixed arrow; it never rotates at run time, but the user may change it.</summary>
    public Direction Direction { get; internal set; }

    /// <summary>Positive number of cells the landing sits away from <see cref="Position"/>.</summary>
    public int TransferDistance { get; internal set; }

    public bool Enabled { get; internal set; } = true;

    /// <summary>Geometric landing this pad aims at, ignoring bounds and passability.</summary>
    public Cell Target
    {
        get
        {
            Cell step = Directions.Step(Direction);
            return new Cell(Position.X + step.X * TransferDistance, Position.Y + step.Y * TransferDistance);
        }
    }

    public override string ToString() => $"#{Id} ({Position.X}, {Position.Y}) {Direction}×{TransferDistance}";
}

/// <summary>Why a launcher chain cannot be used as a move.</summary>
public enum LauncherChainError
{
    None,

    /// <summary>The starting pad is disabled (or the cell has no pad at all).</summary>
    Disabled,

    /// <summary>A landing falls outside the map.</summary>
    OutOfBounds,

    /// <summary>The final landing is a wall or another impassable cell.</summary>
    Blocked,

    /// <summary>The chain would fire the same pad twice.</summary>
    Cycle
}

/// <summary>
/// Result of firing one launcher and every pad it forcibly chains into.
/// The facility publishes this; it never decides whether a pathfinder should use it.
/// </summary>
public sealed class LauncherChainResult
{
    private LauncherChainResult(bool success, Cell? finalCell, List<int> ids, List<Cell> landings,
        int distance, LauncherChainError error, string? note)
    {
        Success = success;
        FinalCell = finalCell;
        TriggeredLauncherIds = ids;
        LandingPositions = landings;
        GeometricDistance = distance;
        Error = error;
        Note = note;
    }

    public bool Success { get; }

    /// <summary>Final ordinary landing; null when the chain fails.</summary>
    public Cell? FinalCell { get; }

    /// <summary>Pads fired, first choice included, in firing order.</summary>
    public IReadOnlyList<int> TriggeredLauncherIds { get; }

    /// <summary>Landing cell after each fired pad; the last entry equals <see cref="FinalCell"/> on success.</summary>
    public IReadOnlyList<Cell> LandingPositions { get; }

    /// <summary>Sum of every fired pad's transfer distance; geometry only, never a path cost.</summary>
    public int GeometricDistance { get; }

    public LauncherChainError Error { get; }

    public string? Note { get; }

    internal static LauncherChainResult Failed(LauncherChainError error, string note,
        List<int> ids, List<Cell> landings, int distance)
        => new(false, null, ids, landings, distance, error, note);

    internal static LauncherChainResult Reached(Cell finalCell, List<int> ids, List<Cell> landings, int distance)
        => new(true, finalCell, ids, landings, distance, LauncherChainError.None, null);
}

/// <summary>
/// Directional launcher facility. A pad cell stays ordinary walkable terrain: the
/// unit may cross it in any legal direction without firing it. When the unit stands
/// on any of the four cells next to a pad it additionally gets one optional special
/// move whose landing is the pad's target; a landing that is itself an enabled pad
/// fires it again, and so on until the chain ends on an ordinary walkable cell.
///
/// The facility only publishes moves and their firing record. Chains are resolved
/// against the map as it is right now (no cache), so every edit is seen by the next
/// search. Cost is published as 0: transfer geometry is never a path cost, each
/// algorithm prices a jump with its own model.
/// </summary>
public sealed class DirectionalLauncherFacility
{
    private readonly List<DirectionalLauncher> _launchers = new();
    private readonly Dictionary<Cell, DirectionalLauncher> _byCell = new();
    private int _nextId = 1;

    public IReadOnlyList<DirectionalLauncher> Launchers => _launchers;

    /// <summary>Places a pad on a map cell; it never paints the cell.</summary>
    public bool Add(GridMap map, Cell cell, Direction direction, int distance, out string? note)
    {
        note = null;

        if (map is null)
        {
            note = "地图为空";
            return false;
        }

        if (!map.InBounds(cell))
        {
            note = $"指向跳板超出地图边界：({cell.X}, {cell.Y})";
            return false;
        }

        if (cell == map.Start || cell == map.Goal)
        {
            note = "起点与终点不能放置指向跳板";
            return false;
        }

        if (_byCell.ContainsKey(cell))
        {
            note = "该格已有指向跳板";
            return false;
        }

        if (distance < 1)
        {
            note = "转移距离必须是正整数";
            return false;
        }

        DirectionalLauncher launcher = new(_nextId++, cell, direction, distance);
        _launchers.Add(launcher);
        _byCell[cell] = launcher;
        return true;
    }

    /// <summary>Removes the pad on this cell; the map needs no repair.</summary>
    public bool RemoveAt(Cell cell)
    {
        if (!_byCell.TryGetValue(cell, out DirectionalLauncher? launcher)) return false;

        _byCell.Remove(cell);
        _launchers.Remove(launcher);
        return true;
    }

    /// <summary>Forgets every pad; used when a new map replaces the old one.</summary>
    public void Reset()
    {
        _launchers.Clear();
        _byCell.Clear();
    }

    public DirectionalLauncher? At(Cell cell)
        => _byCell.TryGetValue(cell, out DirectionalLauncher? launcher) ? launcher : null;

    public bool IsLauncher(Cell cell) => _byCell.ContainsKey(cell);

    public bool SetDirection(int id, Direction direction)
        => Update(id, launcher => launcher.Direction = direction);

    /// <summary>Changes the transfer distance; non-positive values are refused.</summary>
    public bool SetDistance(int id, int distance)
        => distance >= 1 && Update(id, launcher => launcher.TransferDistance = distance);

    public bool SetEnabled(int id, bool enabled)
        => Update(id, launcher => launcher.Enabled = enabled);

    /// <summary>
    /// Fires <paramref name="startLauncher"/> and every enabled pad it lands on.
    /// Bounds and passability are checked on landings only, never on crossed cells.
    /// </summary>
    public LauncherChainResult ResolveChain(GridMap map, DirectionalLauncher startLauncher)
    {
        var ids = new List<int>();
        var landings = new List<Cell>();
        int distance = 0;

        if (map is null)
            return LauncherChainResult.Failed(LauncherChainError.OutOfBounds, "地图为空", ids, landings, distance);

        if (!startLauncher.Enabled)
            return LauncherChainResult.Failed(LauncherChainError.Disabled, "指向跳板已禁用", ids, landings, distance);

        var visited = new HashSet<int>();
        DirectionalLauncher current = startLauncher;

        while (true)
        {
            if (!visited.Add(current.Id))
                return LauncherChainResult.Failed(LauncherChainError.Cycle, "跳板连锁出现循环",
                    ids, landings, distance);

            if (current.TransferDistance < 1)
                return LauncherChainResult.Failed(LauncherChainError.OutOfBounds, "转移距离必须是正整数",
                    ids, landings, distance);

            Cell target = current.Target;
            ids.Add(current.Id);
            distance += current.TransferDistance;

            if (!map.InBounds(target))
                return LauncherChainResult.Failed(LauncherChainError.OutOfBounds,
                    $"跳板 #{current.Id} 的落点越界：({target.X}, {target.Y})", ids, landings, distance);

            landings.Add(target);

            DirectionalLauncher? next = At(target);
            if (next is { Enabled: true })
            {
                // A landing on a live pad is forced: the chain may not stop or be interrupted.
                current = next;
                continue;
            }

            // Disabled pads and plain cells both end the chain; only passability is checked.
            if (map.IsWall(target))
                return LauncherChainResult.Failed(LauncherChainError.Blocked,
                    $"跳板 #{ids[^1]} 的落点不可通行：({target.X}, {target.Y})", ids, landings, distance);

            return LauncherChainResult.Reached(target, ids, landings, distance);
        }
    }

    /// <summary>Convenience overload: fire the pad placed on <paramref name="cell"/>.</summary>
    public LauncherChainResult ResolveChain(GridMap map, Cell cell)
    {
        DirectionalLauncher? launcher = At(cell);
        if (launcher is not null) return ResolveChain(map, launcher);

        return LauncherChainResult.Failed(LauncherChainError.Disabled, "该格没有指向跳板",
            new List<int>(), new List<Cell>(), 0);
    }

    /// <summary>
    /// Every launcher move the unit may pick while standing on <paramref name="cell"/>:
    /// one jump for the pad on that very cell and one for each enabled pad in the four
    /// neighbouring cells. Firing starts at the pad itself, so a unit beside a pad may
    /// use it without stepping on it first. Failed chains are dropped.
    /// </summary>
    public IReadOnlyList<SpecialTransition> TransitionsFrom(GridMap map, Cell cell)
    {
        if (map is null || !map.InBounds(cell) || map.IsWall(cell))
            return Array.Empty<SpecialTransition>();

        var transitions = new List<SpecialTransition>();

        foreach (DirectionalLauncher launcher in _launchers)
        {
            if (!launcher.Enabled) continue;
            if (!map.InBounds(launcher.Position) || map.IsWall(launcher.Position)) continue;
            if (cell != launcher.Position && !IsAdjacent(cell, launcher.Position)) continue;

            LauncherChainResult chain = ResolveChain(map, launcher);
            if (!chain.Success || chain.FinalCell is not { } destination) continue;

            // A jump that lands where the unit already stands moves nothing.
            if (destination == cell) continue;

            transitions.Add(new SpecialTransition(
                destination, TransitionType.Launcher, 0,
                0, 0, chain.TriggeredLauncherIds, chain.LandingPositions));
        }

        return transitions;
    }

    /// <summary>Pads in placement order; the canvas draws one arrow each.</summary>
    public IReadOnlyList<Cell> Positions()
    {
        var cells = new List<Cell>(_launchers.Count);
        foreach (DirectionalLauncher launcher in _launchers) cells.Add(launcher.Position);
        return cells;
    }

    /// <summary>
    /// Wraps another probe: its normal moves and its special moves stay, launcher
    /// jumps are appended. The map is handed in because a probe does not expose it.
    /// </summary>
    public IPathProbe ApplyTo(IPathProbe inner, GridMap map) => new LauncherProbe(inner, this, map);

    /// <summary>Probe with no other facility on top.</summary>
    public IPathProbe CreateProbe(GridMap map) => ApplyTo(new PlainProbe(map), map);

    private bool Update(int id, Action<DirectionalLauncher> change)
    {
        foreach (DirectionalLauncher launcher in _launchers)
        {
            if (launcher.Id != id) continue;

            change(launcher);
            return true;
        }

        return false;
    }

    private static bool IsAdjacent(Cell left, Cell right)
        => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y) == 1;

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

    private sealed class LauncherProbe : IPathProbe
    {
        private readonly IPathProbe _inner;
        private readonly DirectionalLauncherFacility _facility;
        private readonly GridMap _map;

        public LauncherProbe(IPathProbe inner, DirectionalLauncherFacility facility, GridMap map)
        {
            _inner = inner;
            _facility = facility;
            _map = map;
        }

        public IEnumerable<Cell> NormalNeighbours(Cell cell) => _inner.NormalNeighbours(cell);

        public IEnumerable<SpecialTransition> SpecialTransitions(Cell cell)
        {
            foreach (SpecialTransition transition in _inner.SpecialTransitions(cell))
                yield return transition;

            foreach (SpecialTransition transition in _facility.TransitionsFrom(_map, cell))
                yield return transition;
        }

        public bool IsOneWay(Cell cell) => _inner.IsOneWay(cell);

        public IEnumerable<Cell> ExitsAfter(Cell entry, Cell cell) => _inner.ExitsAfter(entry, cell);

        public IPathProbe For(GridMap map) => new LauncherProbe(_inner.For(map), _facility, map);
    }
}
