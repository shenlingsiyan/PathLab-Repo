using System;
using System.Collections.Generic;
using PathLab.Algorithms.FarthestCell02;
using PathLab.Algorithms.MinCost01;
using PathLab.Algorithms.ShortestSteps00;
using PathLab.Core;

namespace PathLab.Algorithms.FarthestLoiter03;

/// <summary>
/// "Loiter around the farthest point" route, built from three existing modules:
/// 1. farthest reachable cell from a reference point (reverse BFS, 02);
/// 2. spawn to that cell with minimum entry cost (Dijkstra, 01);
/// 3. that cell back to the entrance with minimum moves (BFS, 00).
/// The reference point is supplied by the carrier per robot; null means the map goal
/// (the monster entrance). It may be open ground or an obstacle: 02 seeds the reverse
/// BFS from the obstacle's open neighbours. The route is a plain
/// Start -> farthest -> Goal path, so the robot plays it with the same playback
/// machinery as every other module.
/// </summary>
public sealed class FarthestLoiterPathfinder : IPathfinder, IReferencePointModule, ITransitionAware
{
    public string Id => "03-farthest-loiter";

    public string Name => "距参考点最远周旋算法";

    public string Summary => "去程最小代价抵达距参考点最远的格子，返程最短路程回到入怪口。";

    public SearchObjective Objective => SearchObjective.Steps;

    /// <summary>Two legs with different objectives, so no single metric is optimal.</summary>
    public bool Optimal => false;

    public Cell? ReferencePoint { get; set; }

    /// <summary>Optional map facilities (portals); forwarded to 02 and to both legs.</summary>
    public IPathProbe? Probe { get; set; }

    public Cell? UsedReference { get; private set; }

    public Cell? FarthestPoint { get; private set; }

    public SearchResult Search(GridMap map)
    {
        UsedReference = null;
        FarthestPoint = null;

        if (map is null) return Fail("地图为空");

        // The reference may be open ground or an obstacle (a gate carved into a wall);
        // 02 handles both and reports a walled-in reference itself.
        Cell reference = ReferencePoint ?? map.Goal;
        if (!map.InBounds(reference)) return Fail("参考点超出地图边界");

        FarthestPointResult farthest =
            new FarthestFromTargetFinder { Probe = Probe }.Find(map, reference);
        if (!farthest.Found) return Fail(farthest.Note ?? "没有可达格子");

        UsedReference = reference;
        FarthestPoint = farthest.Cell;

        // Leg 1: spawn -> farthest cell, minimum entry cost.
        GridMap outboundMap = map.Clone();
        outboundMap.Start = map.Start;
        outboundMap.Goal = farthest.Cell;
        SearchResult outbound = new MinCostPathfinder { Probe = Probe?.For(outboundMap) }
            .Search(outboundMap);

        if (!outbound.Found)
            return Fail("出怪口无法抵达最远点");

        // Leg 2: farthest cell -> entrance, minimum moves.
        SearchResult inbound = SearchReturn(map, outbound.Path, farthest.Cell);

        if (!inbound.Found)
            return Fail("最远点无法返回入怪口");

        var path = new List<Cell>(outbound.Path.Count + inbound.Path.Count - 1);
        path.AddRange(outbound.Path);
        for (int index = 1; index < inbound.Path.Count; index++)
            path.Add(inbound.Path[index]);

        var explored = new List<Cell>(outbound.Explored.Count + inbound.Explored.Count);
        explored.AddRange(outbound.Explored);
        explored.AddRange(inbound.Explored);

        return new SearchResult
        {
            Found = true,
            Path = path,
            Explored = explored,
            Cost = PathCost(map, path)
        };
    }

    /// <summary>
    /// Return leg: fewest moves from the turn cell to the entrance. Cells already used on the
    /// way out are closed first, so the stitched route stays a simple walk. With facilities the
    /// only way back can be through those cells, so the untouched map is the fallback.
    /// </summary>
    private SearchResult SearchReturn(GridMap map, List<Cell> outbound, Cell turn)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            GridMap legMap = map.Clone();
            legMap.Start = turn;
            legMap.Goal = map.Goal;

            if (pass == 0)
                foreach (Cell cell in outbound)
                {
                    if (cell == turn || cell == map.Goal) continue;
                    legMap.SetCost(cell, GridMap.Wall);
                }

            SearchResult leg = new ShortestStepsPathfinder { Probe = Probe?.For(legMap) }
                .Search(legMap);

            if (leg.Found) return leg;
        }

        return new SearchResult { Found = false, Note = "最远点无法返回入怪口" };
    }

    /// <summary>
    /// Price of the whole route. A portal jump is priced the same way 01 prices it:
    /// the portal fee plus the terrain price of the cell it lands on.
    /// </summary>
    private int PathCost(GridMap map, List<Cell> path)
    {
        int total = 0;

        for (int index = 1; index < path.Count; index++)
        {
            Cell from = path[index - 1];
            Cell to = path[index];
            total += map.CostAt(to);

            if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) == 1) continue;

            total += JumpFee(from, to);
        }

        return total;
    }

    /// <summary>Fee the current probe charges for this jump; 0 when no probe is attached.</summary>
    private int JumpFee(Cell from, Cell to)
    {
        if (Probe is null) return 0;

        int fee = int.MaxValue;
        foreach (SpecialTransition jump in Probe.SpecialTransitions(from))
            if (jump.Destination == to && jump.Cost < fee)
                fee = jump.Cost;

        return fee == int.MaxValue ? 0 : fee;
    }

    private static SearchResult Fail(string note) => new() { Found = false, Note = note };
}
