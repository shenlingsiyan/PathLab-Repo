using System;
using System.Collections.Generic;

namespace PathLab.Core;

/// <summary>
/// One launcher move actually present on a search result: where it started, where it
/// ended, every pad it fired and every landing in between. The runtime needs all of
/// that because a coordinate-only path cannot replay forced chains.
/// </summary>
public readonly record struct LauncherJump(
    Cell From,
    Cell Destination,
    IReadOnlyList<int> TriggeredLauncherIds,
    IReadOnlyList<Cell> LandingPositions);

/// <summary>
/// Rebuilds the launcher events behind a finished path. Algorithms keep returning
/// plain coordinates, so this reads the same probe once more and matches every
/// non-adjacent step to the transition that produced it. It changes no search,
/// no cost model and no path node.
/// </summary>
public sealed class LauncherPathPlan
{
    private LauncherPathPlan(List<Cell> path, List<LauncherJump> jumps, bool resolved, string? note)
    {
        Path = path;
        Jumps = jumps;
        Resolved = resolved;
        Note = note;
    }

    /// <summary>Ordinary path nodes, unchanged.</summary>
    public IReadOnlyList<Cell> Path { get; }

    /// <summary>Launcher moves in execution order.</summary>
    public IReadOnlyList<LauncherJump> Jumps { get; }

    /// <summary>True when every jump step matched one published launcher chain.</summary>
    public bool Resolved { get; }

    public string? Note { get; }

    public static LauncherPathPlan Build(GridMap map, IPathProbe? probe, IReadOnlyList<Cell>? path)
    {
        var jumps = new List<LauncherJump>();
        var nodes = new List<Cell>();

        if (path is null || path.Count == 0)
            return new LauncherPathPlan(nodes, jumps, false, "路径为空");

        nodes.AddRange(path);

        if (probe is null)
            return new LauncherPathPlan(nodes, jumps, true, null);

        for (int index = 1; index < path.Count; index++)
        {
            Cell from = path[index - 1];
            Cell to = path[index];

            if (map.InBounds(from) && map.InBounds(to)
                && Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) == 1)
                continue; // an ordinary 4-neighbour step

            SpecialTransition? jump = FindLauncher(probe, from, to);
            if (jump is null)
            {
                // Another facility (a portal, for example) owns this step; the launcher
                // record simply skips it instead of claiming the whole route is broken.
                if (AnySpecial(probe, from, to)) continue;

                return new LauncherPathPlan(nodes, jumps, false,
                    $"无法解释从 ({from.X}, {from.Y}) 到 ({to.X}, {to.Y}) 的特殊移动");
            }

            SpecialTransition matched = jump.Value;

            jumps.Add(new LauncherJump(
                from, matched.Destination,
                matched.TriggeredLauncherIds ?? Array.Empty<int>(),
                matched.LandingPositions ?? Array.Empty<Cell>()));
        }

        return new LauncherPathPlan(nodes, jumps, true, null);
    }

    /// <summary>Fills <see cref="SearchResult.LauncherJumps"/> from the same probe.</summary>
    public static LauncherPathPlan Attach(GridMap map, IPathProbe? probe, SearchResult result)
    {
        LauncherPathPlan plan = Build(map, probe, result.Path);
        result.LauncherJumps = new List<LauncherJump>(plan.Jumps);
        return plan;
    }

    /// <summary>
    /// Picks the launcher transition that lands on <paramref name="to"/>. Placement
    /// order makes the choice deterministic when two pads offer the same landing.
    /// </summary>
    private static SpecialTransition? FindLauncher(IPathProbe probe, Cell from, Cell to)
    {
        SpecialTransition? match = null;

        foreach (SpecialTransition transition in probe.SpecialTransitions(from))
        {
            if (transition.Destination != to) continue;
            if (transition.Type != TransitionType.Launcher) continue;

            match = transition;
            break;
        }

        return match;
    }

    /// <summary>True when any facility publishes a special step between these two cells.</summary>
    private static bool AnySpecial(IPathProbe probe, Cell from, Cell to)
    {
        foreach (SpecialTransition transition in probe.SpecialTransitions(from))
            if (transition.Destination == to)
                return true;

        return false;
    }
}
