using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>What a module claims to optimize.</summary>
public enum SearchObjective
{
    /// <summary>Number of moves, cell prices ignored.</summary>
    Steps,

    /// <summary>Sum of entry prices, move count not optimized.</summary>
    Cost,

    /// <summary>A weighted score to maximize; the formula is defined by the module README.</summary>
    Score
}

/// <summary>
/// Contract every pathfinding module implements.
/// A module is one folder under /algorithms; the carrier compiles a copy of it.
/// </summary>
public interface IPathfinder
{
    /// <summary>Stable sort key, for example "00-shortest-steps".</summary>
    string Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>One-line explanation shown in the UI.</summary>
    string Summary { get; }

    /// <summary>What the module optimizes: fewer steps, lower price, or a higher score.</summary>
    SearchObjective Objective { get; }

    /// <summary>True when the module is optimal for <see cref="Objective"/>.</summary>
    bool Optimal { get; }

    /// <summary>
    /// True when the module optimizes under an extra constraint (for example "the route
    /// must touch a given obstacle cluster"), so its cost may legitimately exceed the
    /// unconstrained optimum. Self-test skips the plain-optimum comparison for these.
    /// </summary>
    bool Constrained => false;

    SearchResult Search(GridMap map);
}

/// <summary>Result of one search. The robot fills ElapsedMs and drives the animation.</summary>
public sealed class SearchResult
{
    public bool Found { get; set; }
    public int Cost { get; set; }
    public List<Cell> Path { get; set; } = new();
    public List<Cell> Explored { get; set; } = new();
    public double ElapsedMs { get; set; }
    public string? Note { get; set; }

    /// <summary>Target-function value of <see cref="Path"/>; 0 for step and cost modules.</summary>
    public double Score { get; set; }

    /// <summary>True when this result is proven optimal; score modules only.</summary>
    public bool IsOptimal { get; set; }

    /// <summary>How the search ended (exact, bounded, approximate, no route); score modules only.</summary>
    public string? TerminationReason { get; set; }

    /// <summary>
    /// Reference cells this path enters (start and goal excluded); 07 only, 0 elsewhere.
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// Directional launcher moves used by <see cref="Path"/>, in order. The carrier fills
    /// this after the search; an empty list means the route never fired a launcher.
    /// </summary>
    public List<LauncherJump> LauncherJumps { get; set; } = new();

    /// <summary>Effective taunt points this search had to visit; 0 for modules without the constraint.</summary>
    public int TauntRequiredCount { get; set; }

    /// <summary>Effective taunt points the finished path actually visited.</summary>
    public int TauntVisitedCount { get; set; }

    /// <summary>Effective taunt cells the finished path missed; empty on a legal route.</summary>
    public List<Cell> MissedTauntCells { get; set; } = new();
}
