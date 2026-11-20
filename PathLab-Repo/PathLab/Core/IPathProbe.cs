using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>What a cell means once map facilities are layered over it.</summary>
public enum CellKind
{
    /// <summary>Ordinary walkable cell.</summary>
    NormalWalkable,

    /// <summary>Wall, or outside the map.</summary>
    Blocked,

    /// <summary>Facility cell: never walkable, reachable only through its own transitions.</summary>
    Portal
}

/// <summary>Kind of a special (non-geometric) move supplied by a facility.</summary>
public enum TransitionType
{
    /// <summary>Chaotic portal jump.</summary>
    Teleport,

    /// <summary>Directional launcher transfer, possibly a forced chain of several pads.</summary>
    Launcher
}

/// <summary>One extra connection a facility offers from the current cell.</summary>
/// <param name="Destination">Cell the probe lands on, always a normal walkable cell.</param>
/// <param name="Type">Why this connection exists.</param>
/// <param name="Cost">Facility-side surcharge, the portal fee alone; the algorithm adds normal cell costs.</param>
/// <param name="SourcePortalId">Portal used as the entrance.</param>
/// <param name="DestinationPortalId">Portal used as the exit.</param>
/// <param name="TriggeredLauncherIds">
/// Launchers fired by this move, first choice included, in firing order; null for portals.
/// </param>
/// <param name="LandingPositions">
/// Landing cell after each launcher fired; null for portals. The last entry equals <paramref name="Destination"/>.
/// </param>
public readonly record struct SpecialTransition(
    Cell Destination,
    TransitionType Type,
    int Cost,
    int SourcePortalId = 0,
    int DestinationPortalId = 0,
    IReadOnlyList<int>? TriggeredLauncherIds = null,
    IReadOnlyList<Cell>? LandingPositions = null);

/// <summary>
/// How a probe reads the map: ordinary 4-neighbour moves and special facility moves.
/// Each algorithm reads both lists and decides itself what a move costs.
/// </summary>
public interface IPathProbe
{
    IEnumerable<Cell> NormalNeighbours(Cell cell);

    IEnumerable<SpecialTransition> SpecialTransitions(Cell cell);

    /// <summary>
    /// The same facilities bound to another map instance. Modules that search a clone
    /// (for example a leg with a different goal) use this instead of the original probe.
    /// </summary>
    IPathProbe For(GridMap map);

    /// <summary>
    /// True when this cell is a one-way passage cell. Walking into such a cell
    /// commits the walker to one straight exit, so searches must split states
    /// by entry and read exits through <see cref="ExitsAfter"/>.
    /// </summary>
    bool IsOneWay(Cell cell) => false;

    /// <summary>
    /// Cells reachable from <paramref name="cell"/> in one walking move, given where
    /// the walker came from. On a passage cell a walk-in entry (4-adjacent) may only
    /// leave straight through; an arrival without geometry (teleport landing, or the
    /// entry equal to the cell) may leave anywhere except the tail side. Every other
    /// cell simply returns <see cref="NormalNeighbours"/>.
    /// </summary>
    IEnumerable<Cell> ExitsAfter(Cell entry, Cell cell) => NormalNeighbours(cell);
}

/// <summary>
/// Optional capability for path modules that accept facility connections.
/// The carrier sets a probe after creating the module; modules without this
/// interface keep their plain behaviour untouched.
/// </summary>
public interface ITransitionAware
{
    IPathProbe? Probe { get; set; }
}

/// <summary>
/// Optional capability for path modules that enforce the taunt-point constraint.
/// The carrier builds one <see cref="TauntPlan"/> per search and writes it here;
/// null (or an empty plan) keeps the module's plain search untouched.
/// </summary>
public interface ITauntAware
{
    TauntPlan? Taunt { get; set; }
}
