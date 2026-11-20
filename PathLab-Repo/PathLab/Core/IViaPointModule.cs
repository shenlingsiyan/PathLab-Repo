using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// Optional capability for a constrained module whose route must pass one of several
/// marker cells: the walkable ring around an obstacle cluster, and the ring cell the
/// last search actually used. The carrier draws the ring as hollow marks and the used
/// cell as its `F` badge. Modules without this interface are unaffected.
/// </summary>
public interface IViaPointModule
{
    /// <summary>Deduplicated walkable neighbours of the last cluster, row-major; empty before the first search.</summary>
    IReadOnlyList<Cell> RingCells { get; }

    /// <summary>The ring cell the last route passed through; <c>null</c> when there is none.</summary>
    Cell? ViaCell { get; }
}
