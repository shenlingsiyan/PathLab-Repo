using System;

namespace PathLab.Core;

/// <summary>
/// Optional capability for path modules that need one user-chosen reference cell
/// (for example "the farthest cell from the monster entrance") and report the
/// two markers behind their last search, so the canvas can draw them.
/// Modules that do not implement this are unaffected; the carrier simply skips them.
/// </summary>
public interface IReferencePointModule
{
    /// <summary>Reference cell chosen by the user; <c>null</c> asks the module for its default.</summary>
    Cell? ReferencePoint { get; set; }

    /// <summary>Reference actually used by the last search; <c>null</c> before the first search.</summary>
    Cell? UsedReference { get; }

    /// <summary>Farthest reachable cell found by the last search; <c>null</c> when there is none.</summary>
    Cell? FarthestPoint { get; }
}
