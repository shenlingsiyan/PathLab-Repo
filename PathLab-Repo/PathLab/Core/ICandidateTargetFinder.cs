using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>Why a target-candidate query failed. <see cref="None"/> means it succeeded.</summary>
public enum CandidateTargetError
{
    None,
    NullMap,
    EntranceOutOfBounds,
    Unreachable,
    InvalidAlpha,
    InvalidRadiusRange,
    RadiusOverflow
}

/// <summary>
/// Query module: given an entrance and a ratio, return every traversable, reachable cell
/// whose Euclidean distance to the entrance sits in an integer radius band
/// (default: exactly floor(shortest steps * alpha)).
/// It answers "which cells may become targets", not "how do I walk there", so it is not
/// an <see cref="IPathfinder"/> and never appears in the algorithm list.
/// </summary>
public interface ICandidateTargetFinder
{
    /// <summary>Stable sort key, for example "04-radius-candidates".</summary>
    string Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>One-line explanation.</summary>
    string Summary { get; }

    /// <summary>Suggested starting value for the ratio, 0.5.</summary>
    double DefaultAlpha { get; }

    /// <summary>
    /// <paramref name="alpha"/> must be a finite non-negative number;
    /// the base radius is <c>floor(steps * alpha)</c>, with no lower or upper clamp.
    /// The optional integer bounds restrict the accepted distance to a closed band:
    /// equal bounds are an exact circle, different bounds are a ring. Both null means
    /// "use the base radius for both ends", which is the plain circle.
    /// </summary>
    CandidateTargetsResult Find(GridMap map, Cell entrance, double alpha,
        int? minRadius = null, int? maxRadius = null);
}

/// <summary>Outcome of one target-candidate query.</summary>
public sealed class CandidateTargetsResult
{
    /// <summary>True even when <see cref="Candidates"/> is empty: an empty circle is a legal answer.</summary>
    public bool Success { get; set; }

    public CandidateTargetError Error { get; set; } = CandidateTargetError.None;

    /// <summary>Human-readable reason; null on a successful query.</summary>
    public string? Note { get; set; }

    /// <summary>Fewest moves from the spawn to the entrance (edges, not cells).</summary>
    public int BasePathSteps { get; set; }

    /// <summary>Ratio used by this query.</summary>
    public double Alpha { get; set; }

    /// <summary><c>floor(BasePathSteps * Alpha)</c>; zero is legal.</summary>
    public int Radius { get; set; }

    /// <summary>Lower bound actually used for the distance band.</summary>
    public int MinRadius { get; set; }

    /// <summary>Upper bound actually used for the distance band; equal to <see cref="MinRadius"/> means a circle.</summary>
    public int MaxRadius { get; set; }

    /// <summary>True when the two bounds differ, i.e. the filter keeps a ring.</summary>
    public bool IsRing => MinRadius != MaxRadius;

    /// <summary>All qualifying cells, row-major (y, then x).</summary>
    public List<Cell> Candidates { get; set; } = new();

    public int CandidateCount => Candidates.Count;
}
