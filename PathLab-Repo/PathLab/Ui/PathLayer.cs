using System.Drawing;
using PathLab.Core;

namespace PathLab.Ui;

/// <summary>
/// One algorithm overlay: its last result, the robot's current cell and the
/// colour used for its explored dots, path and robot.
/// <see cref="TurnIndex"/> splits the path into an outbound leg and a return leg;
/// modules that do not turn pass -1 and are drawn as a single line.
/// </summary>
public sealed class PathLayer
{
    public PathLayer(SearchResult? result, Cell robotCell, Color color, int turnIndex = -1)
    {
        Result = result;
        RobotCell = robotCell;
        Color = color;
        TurnIndex = turnIndex;
    }

    public SearchResult? Result { get; }

    public Cell RobotCell { get; }

    public Color Color { get; }

    /// <summary>Index in <c>Result.Path</c> where the return leg starts, or -1.</summary>
    public int TurnIndex { get; }
}
