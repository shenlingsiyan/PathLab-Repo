using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// Optional capability for path modules that take a *set* of user-chosen reference
/// cells (07 treats them as soft obstacles). The carrier owns the list and pushes the
/// same set into every robot that implements this, so nothing else in the project has
/// to know about it.
/// </summary>
public interface IReferenceCellSetModule
{
    /// <summary>Reference cells chosen by the user; an empty list means "no references".</summary>
    IReadOnlyList<Cell> ReferenceCells { get; set; }

    /// <summary>
    /// References actually used by the last search: in-bounds and de-duplicated.
    /// Cells that coincide with walls stay in this list but are never reachable.
    /// </summary>
    IReadOnlyList<Cell> UsedReferenceCells { get; }
}
