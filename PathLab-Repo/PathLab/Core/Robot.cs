using System;
using System.Diagnostics;

namespace PathLab.Core;

/// <summary>
/// The robot owns the plan and a cursor over it. The UI only advances the cursor,
/// so every algorithm is demonstrated with the exact same playback mechanism.
/// </summary>
public sealed class Robot
{
    public Robot(GridMap map, IPathfinder algorithm)
    {
        Map = map;
        Algorithm = algorithm;
    }

    public GridMap Map { get; private set; }
    public IPathfinder Algorithm { get; private set; }
    public SearchResult? Result { get; private set; }
    public int StepIndex { get; private set; }
    public string? Error { get; private set; }

    public bool HasPlan => Result is { Found: true } && Result.Path.Count > 0;
    public bool AtGoal => HasPlan && StepIndex >= Result!.Path.Count - 1;
    public int MovesDone => HasPlan ? Math.Min(StepIndex, Result!.Path.Count - 1) : 0;

    public Cell Position
    {
        get
        {
            if (HasPlan)
                return Result!.Path[Math.Clamp(StepIndex, 0, Result.Path.Count - 1)];
            return Map.Start;
        }
    }

    public void SetMap(GridMap map)
    {
        Map = map;
        Result = null;
        StepIndex = 0;
        Error = null;
    }

    public void SetAlgorithm(IPathfinder algorithm)
    {
        Algorithm = algorithm;
        Result = null;
        StepIndex = 0;
        Error = null;
    }

    public void Plan()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Result = Algorithm.Search(Map);
            Error = null;
        }
        catch (Exception ex)
        {
            Result = new SearchResult { Found = false, Note = ex.Message };
            Error = ex.Message;
        }

        stopwatch.Stop();
        if (Result is not null)
            Result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        StepIndex = 0;
    }

    /// <summary>Advance one cell. Returns false when the robot cannot move.</summary>
    public bool Advance()
    {
        if (!HasPlan || AtGoal) return false;
        StepIndex++;
        return true;
    }

    public void Reset() => StepIndex = 0;
}
