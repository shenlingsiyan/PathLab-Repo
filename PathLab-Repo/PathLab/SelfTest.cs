using System;
using System.Collections.Generic;
using System.Linq;
using PathLab.Algorithms.FarthestCell02;
using PathLab.Algorithms.FarthestLoiter03;
using PathLab.Algorithms.MinCost01;
using PathLab.Algorithms.MaxPerpendicular06;
using PathLab.Algorithms.MinReference07;
using PathLab.Algorithms.ObstacleCluster05;
using PathLab.Algorithms.RadiusCandidates04;
using PathLab.Algorithms.ShortestSteps00;
using PathLab.Core;

namespace PathLab;

/// <summary>Headless verification: run `PathLab.exe --selftest`.</summary>
internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();

        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok) failures.Add(label);
        }

        // Every score route must be a legal simple walk whose reported score survives
        // an independent re-scoring of the final path.
        void CheckScoreRoute(GridMap map, Cell marker, SearchResult result, string label)
        {
            Check(result.Found, $"perpendicular {label}: route found");
            if (!result.Found) return;

            Check(IsValidPath(map, result.Path), $"perpendicular {label}: 4-neighbour wall-free route");
            Check(result.Path.Distinct().Count() == result.Path.Count,
                $"perpendicular {label}: no cell repeats");
            Check(Math.Abs(result.Score - MaxPerpendicularPathfinder.CalculatePathScore(result.Path, marker)) < 1e-9,
                $"perpendicular {label}: score {result.Score} equals the independent re-score");
            Check(result.Cost == SumPathCost(map, result.Path),
                $"perpendicular {label}: reported cost matches the path");
            Check(result.TerminationReason is not null, $"perpendicular {label}: termination reason recorded");
        }

        // Every reference route must be a legal simple walk whose crossing count survives
        // an independent re-count of the final path.
        SearchResult ReferenceRoute(GridMap map, IReadOnlyList<Cell>? references, string label)
        {
            var module = new MinimumReferenceTraversalPathfinder
            {
                ReferenceCells = references ?? Array.Empty<Cell>()
            };

            SearchResult result = module.Search(map);

            Check(result.Found, $"reference {label}: route found");
            if (!result.Found) return result;

            Check(MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(
                      result, map, module.ReferenceCells) is null,
                $"reference {label}: independent validation agrees");
            Check(result.Path.Distinct().Count() == result.Path.Count,
                $"reference {label}: no cell repeats");
            Check(result.IsOptimal, $"reference {label}: solves the lexicographic optimum");
            Check(result.ReferenceCount == MinimumReferenceTraversalPathfinder.CountReferences(
                      result.Path, map, module.ReferenceCells),
                $"reference {label}: crossings recomputed from the path ({result.ReferenceCount})");
            Check(result.Explored.Count > 0, $"reference {label}: expanded cells recorded");
            Check(result.TerminationReason == "找到最少穿越的最短路径",
                $"reference {label}: termination reason names the optimum");
            return result;
        }

        var algorithms = AlgorithmHost.All;
        Console.WriteLine($"discovered algorithms: {algorithms.Count} "
                          + $"[{string.Join(", ", algorithms.Select(a => a.Id))}]");
        Check(algorithms.Count > 0, "algorithm discovery");

        GridMap testMap = BuildTestMap();
        int referenceCost = ReferenceCost(testMap);
        int referenceSteps = ReferenceSteps(testMap);
        Console.WriteLine($"test map oracles: cost {referenceCost}, steps {referenceSteps}");

        foreach (IPathfinder algorithm in algorithms)
        {
            // Constrained modules need a reference before they can plan anything.
            if (algorithm is IReferencePointModule module)
                module.ReferencePoint = ObstacleCluster.FirstWall(testMap) ?? testMap.Goal;

            SearchResult result = algorithm.Search(testMap);
            Check(result.Found, $"{algorithm.Id}: path found");
            if (!result.Found) continue;

            Check(IsValidPath(testMap, result.Path), $"{algorithm.Id}: 4-neighbour, wall-free path");
            Check(result.Cost == SumPathCost(testMap, result.Path),
                $"{algorithm.Id}: reported cost matches path ({result.Cost})");
            Check(result.Explored.Count > 0, $"{algorithm.Id}: explored order recorded");

            if (!algorithm.Optimal)
            {
                Console.WriteLine($"SKIP  {algorithm.Id}: optimality not claimed");
            }
            else if (algorithm.Constrained)
            {
                Console.WriteLine($"SKIP  {algorithm.Id}: constrained optimum checked separately");
            }
            else if (algorithm.Objective == SearchObjective.Cost)
            {
                Check(result.Cost == referenceCost,
                    $"{algorithm.Id}: optimal cost {result.Cost} == {referenceCost}");
            }
            else
            {
                int moves = result.Path.Count - 1;
                Check(moves == referenceSteps,
                    $"{algorithm.Id}: optimal steps {moves} == {referenceSteps}");
            }
        }

        IPathfinder? stepAlgorithm = algorithms.FirstOrDefault(
            a => a.Optimal && a.Objective == SearchObjective.Steps);
        IPathfinder? costAlgorithm = algorithms.FirstOrDefault(
            a => a.Optimal && a.Objective == SearchObjective.Cost);

        if (stepAlgorithm is not null && costAlgorithm is not null)
        {
            SearchResult stepResult = stepAlgorithm.Search(testMap);
            SearchResult costResult = costAlgorithm.Search(testMap);
            Check(stepResult.Found && costResult.Found, "both objectives reach the goal");

            if (stepResult.Found && costResult.Found)
            {
                int stepMoves = stepResult.Path.Count - 1;
                int costMoves = costResult.Path.Count - 1;

                Check(stepMoves <= costMoves,
                    $"step-optimal uses no more moves ({stepMoves} <= {costMoves})");
                Check(costResult.Cost <= stepResult.Cost,
                    $"cost-optimal is no more expensive ({costResult.Cost} <= {stepResult.Cost})");
                Check(stepMoves < costMoves && costResult.Cost < stepResult.Cost,
                    "objectives diverge on the test map");
            }
        }
        else
        {
            Console.WriteLine("SKIP  objective cross-check: need one Steps and one Cost module");
        }

        if (algorithms.Count > 0)
        {
            var robot = new Robot(testMap, algorithms[0]);
            robot.Plan();
            int steps = 0;
            while (robot.Advance() && steps < 1000) steps++;

            Check(robot.AtGoal, "robot reaches the goal");
            Check(steps == robot.Result!.Path.Count - 1,
                $"robot steps {steps} == path moves {robot.Result.Path.Count - 1}");
            Check(IsValidPath(testMap, robot.Result.Path), "robot follows a valid path");

            robot.Reset();
            Check(robot.Position == testMap.Start && !robot.AtGoal, "robot reset returns to start");
        }

        if (algorithms.Count >= 2)
        {
            var team = algorithms.Take(2)
                .Select(algorithm => new Robot(testMap, AlgorithmHost.Create(algorithm)))
                .ToList();

            Check(team[0].Algorithm != team[1].Algorithm, "compare mode: independent module instances");
            foreach (Robot member in team) member.Plan();

            int guard = 0;
            while (!team.Any(member => member.AtGoal) && guard++ < 1000)
                foreach (Robot member in team) member.Advance();

            Check(team.Any(member => member.AtGoal), "compare mode: first robot finishes");

            Robot? other = team.FirstOrDefault(member => member.HasPlan && !member.AtGoal);
            if (other is not null)
                Check(other.Advance(), "compare mode: other robot keeps moving after the first finishes");

            guard = 0;
            bool moved;
            do
            {
                moved = false;
                foreach (Robot member in team)
                    if (member.Advance()) moved = true;
            }
            while (moved && guard++ < 1000);

            Check(team.All(member => member.AtGoal), "compare mode: both robots finish independently");
            Check(team.All(member => member.MovesDone == member.Result!.Path.Count - 1),
                "compare mode: each robot walks its own path");
        }
        else
        {
            Console.WriteLine("SKIP  compare-mode robot check: need two modules");
        }

        // ---- farthest reachable cell: reverse BFS from a reference cell ----
        var farthestFinder = new FarthestFromTargetFinder();
        var farthestMap = new GridMap(7, 5) { Start = new Cell(0, 2), Goal = new Cell(6, 2) };
        farthestMap.SetCost(new Cell(1, 2), 9); // terrain price must not change move distance

        FarthestPointResult farthest = farthestFinder.Find(farthestMap, farthestMap.Start);
        Check(farthest.Found, "farthest: open map query succeeds");
        Check(farthest.Distance == 8, $"farthest: 7x5 far corner is 8 moves ({farthest.Distance})");
        Check(farthest.Cell == new Cell(6, 0), $"farthest: row-major tie-break picks (6,0) ({farthest.Cell})");
        Check(farthest.Candidates.Count == 2
              && farthest.Candidates[0] == new Cell(6, 0)
              && farthest.Candidates[1] == new Cell(6, 4),
            "farthest: both far corners listed as candidates");
        Check(farthest.DistanceAt(farthestMap.Start) == 0, "farthest: reference cell distance is 0");
        Check(farthest.DistanceAt(new Cell(1, 2)) == 1, "farthest: price-9 cell still counts one move");
        Check(farthest.Candidates.All(cell => farthest.DistanceAt(cell) == farthest.Distance),
            "farthest: every candidate sits on the farthest layer");
        Check(MatchesReferenceDistances(farthestMap, farthestMap.Start, farthest),
            "farthest: distances match an independent BFS");

        FarthestPointResult repeated = farthestFinder.Find(farthestMap, farthestMap.Start);
        Check(repeated.Found && repeated.Cell == farthest.Cell,
            "farthest: repeated calls on the same map are deterministic");

        var walledMap = new GridMap(7, 5) { Start = new Cell(0, 2), Goal = new Cell(0, 4) };
        for (int y = 0; y < walledMap.Height; y++)
            walledMap.SetCost(new Cell(6, y), GridMap.Wall);

        FarthestPointResult isolated = farthestFinder.Find(walledMap, walledMap.Start);
        Check(isolated.Found, "farthest: query still succeeds next to an isolated column");
        Check(isolated.Distance == 7, $"farthest: unreachable column excluded, distance 7 ({isolated.Distance})");
        Check(isolated.Cell == new Cell(5, 0), $"farthest: nearest far corner picked ({isolated.Cell})");
        Check(isolated.Candidates.All(cell => !walledMap.IsWall(cell)),
            "farthest: walls never become candidates");
        Check(isolated.DistanceAt(new Cell(6, 0)) == FarthestPointResult.Unreachable,
            "farthest: isolated cells stay unreachable");

        var singleMap = new GridMap(3, 3) { Start = new Cell(1, 1), Goal = new Cell(1, 1) };
        for (int y = 0; y < singleMap.Height; y++)
        for (int x = 0; x < singleMap.Width; x++)
            if (x != 1 || y != 1)
                singleMap.SetCost(new Cell(x, y), GridMap.Wall);

        FarthestPointResult onlyOne = farthestFinder.Find(singleMap, new Cell(1, 1));
        Check(onlyOne.Found && onlyOne.Cell == new Cell(1, 1)
              && onlyOne.Distance == 0 && onlyOne.Candidates.Count == 1,
            "farthest: single traversable cell returns itself at distance 0");

        Check(!farthestFinder.Find(null!, new Cell(0, 0)).Found, "farthest: null map rejected");
        FarthestPointResult outside = farthestFinder.Find(farthestMap, new Cell(-1, 0));
        Check(!outside.Found, "farthest: out-of-bounds reference cell rejected");
        Check(outside.Note is not null && outside.Note.Contains("边界"),
            "farthest: out-of-bounds reference cell reports its reason");
        // A wall is a legal reference too: the walk steps out of it, so its open
        // neighbours start at distance 1 and the wall itself stays unreachable.
        FarthestPointResult wallReference = farthestFinder.Find(walledMap, new Cell(6, 0));
        Check(wallReference.Found && wallReference.Distance == 10,
            $"farthest: wall reference seeds its open neighbour at distance 1 ({wallReference.Distance})");
        Check(wallReference.DistanceAt(new Cell(5, 0)) == 1,
            "farthest: open neighbour of a wall reference counts one move");
        Check(!walledMap.IsWall(wallReference.Cell),
            "farthest: farthest cell from a wall reference is traversable");
        Check(MatchesReferenceDistances(walledMap, new Cell(6, 0), wallReference),
            "farthest: wall-reference distances match an independent BFS");

        // A wall reference boxed in by walls has nowhere to step out to.
        var sealedMap = new GridMap(5, 5) { Start = new Cell(0, 0), Goal = new Cell(4, 4) };
        foreach (Cell wall in new[] { new Cell(2, 2), new Cell(1, 2), new Cell(3, 2), new Cell(2, 1), new Cell(2, 3) })
            sealedMap.SetCost(wall, GridMap.Wall);

        FarthestPointResult sealedReference = farthestFinder.Find(sealedMap, new Cell(2, 2));
        Check(!sealedReference.Found && sealedReference.Note is not null
              && sealedReference.Note.Contains("四周"),
            "farthest: wall reference with no open neighbour fails with a reason");

        // ---- farthest loiter: spawn -> farthest cell -> entrance ----
        var loiter = new FarthestLoiterPathfinder();
        IPathfinder? loiterModule = algorithms.FirstOrDefault(a => a is IReferencePointModule);
        Check(loiterModule is not null, "loiter: module is discovered by the carrier");

        loiter.ReferencePoint = new Cell(6, 2); // the entrance (goal) of the 7x5 map
        SearchResult loiterRoute = loiter.Search(farthestMap);
        Check(loiterRoute.Found, "loiter: composite route found");
        Check(loiterRoute.Path.Count > 0
              && loiterRoute.Path[0] == farthestMap.Start
              && loiterRoute.Path[^1] == farthestMap.Goal,
            "loiter: route runs from the spawn to the entrance");
        Check(loiter.UsedReference == new Cell(6, 2), "loiter: reports the reference it used");
        Check(loiter.FarthestPoint == new Cell(0, 0), "loiter: turns at the farthest reaching cell");
        Check(loiterRoute.Path.Contains(new Cell(0, 0)), "loiter: farthest cell lies on the route");
        Check(loiterRoute.Path.IndexOf(new Cell(0, 0)) == 2,
            "loiter: outbound leg ends after two moves");
        Check(loiterRoute.Cost == SumPathCost(farthestMap, loiterRoute.Path),
            "loiter: reported cost sums the whole route");
        Check(!loiterRoute.Path.Contains(new Cell(1, 2)),
            "loiter: outbound leg avoids the price-9 cell");

        // The composite must reuse the two existing modules, leg for leg.
        GridMap leg1Map = farthestMap.Clone();
        leg1Map.Start = farthestMap.Start;
        leg1Map.Goal = new Cell(0, 0);
        SearchResult leg1 = new MinCostPathfinder().Search(leg1Map);

        GridMap leg2Map = farthestMap.Clone();
        leg2Map.Start = new Cell(0, 0);
        leg2Map.Goal = farthestMap.Goal;
        SearchResult leg2 = new ShortestStepsPathfinder().Search(leg2Map);

        Check(loiterRoute.Path.Count - 1 == (leg1.Path.Count - 1) + (leg2.Path.Count - 1),
            "loiter: route length equals both legs combined");
        Check(loiterRoute.Cost == leg1.Cost + leg2.Cost,
            $"loiter: route cost equals both legs combined ({loiterRoute.Cost} == {leg1.Cost} + {leg2.Cost})");
        Check(loiterRoute.Explored.Count > 0, "loiter: explored cells recorded");

        loiter.ReferencePoint = null;
        SearchResult defaultReference = loiter.Search(farthestMap);
        Check(loiter.UsedReference == farthestMap.Goal, "loiter: default reference is the entrance");
        Check(defaultReference.Found, "loiter: default-reference route found");

        loiter.ReferencePoint = new Cell(99, 0);
        Check(!loiter.Search(farthestMap).Found, "loiter: out-of-bounds reference rejected");

        // A walled reference is legal: 02 seeds from its open neighbours.
        GridMap walledReference = farthestMap.Clone();
        walledReference.SetCost(new Cell(3, 3), GridMap.Wall);
        loiter.ReferencePoint = new Cell(3, 3);
        SearchResult walledRoute = loiter.Search(walledReference);
        Check(walledRoute.Found && loiter.UsedReference == new Cell(3, 3),
            "loiter: walled reference accepted");
        Check(loiter.FarthestPoint is { } walledTurn && !walledReference.IsWall(walledTurn),
            "loiter: farthest turn from a walled reference stays traversable");
        Check(walledRoute.Found && walledRoute.Path[0] == walledReference.Start
              && walledRoute.Path[^1] == walledReference.Goal,
            "loiter: walled-reference route still runs spawn -> entrance");

        loiter.ReferencePoint = new Cell(2, 2);
        SearchResult sealedRoute = loiter.Search(sealedMap);
        Check(!sealedRoute.Found && sealedRoute.Note is not null && sealedRoute.Note.Contains("四周"),
            "loiter: sealed-in reference fails with a reason");

        var splitMap = new GridMap(7, 5) { Start = new Cell(0, 2), Goal = new Cell(6, 2) };
        for (int y = 0; y < splitMap.Height; y++)
            splitMap.SetCost(new Cell(3, y), GridMap.Wall);

        loiter.ReferencePoint = splitMap.Goal;
        SearchResult splitRoute = loiter.Search(splitMap);
        Check(!splitRoute.Found && splitRoute.Note is not null && splitRoute.Note.Contains("出怪口"),
            "loiter: spawn cut off from the farthest cell fails with a reason");
        Check(loiter.FarthestPoint is not null, "loiter: farthest marker kept even when leg 1 fails");

        // ---- radius candidate targets: exact circle around the entrance ----
        var candidateFinder = new RadiusCandidateFinder();

        var corridorMap = new GridMap(13, 3) { Start = new Cell(0, 0), Goal = new Cell(12, 0) };
        for (int y = 1; y < corridorMap.Height; y++)
        for (int x = 0; x < corridorMap.Width; x++)
            corridorMap.SetCost(new Cell(x, y), GridMap.Wall);

        CandidateTargetsResult basic = candidateFinder.Find(corridorMap, corridorMap.Goal, 0.5);
        Check(basic.Success && basic.BasePathSteps == 12,
            $"candidates: corridor length is 12 edges ({basic.BasePathSteps})");
        Check(basic.Success && basic.Radius == 6,
            $"candidates: floor(12 * 0.5) is 6 ({basic.Radius})");
        Check(basic.Success && basic.CandidateCount == 1 && basic.Candidates[0] == new Cell(6, 0),
            "candidates: radius-6 circle hits the single in-map cell (6,0)");

        var shortMap = new GridMap(7, 2) { Start = new Cell(0, 0), Goal = new Cell(6, 0) };
        for (int x = 0; x < shortMap.Width; x++)
            shortMap.SetCost(new Cell(x, 1), GridMap.Wall);

        CandidateTargetsResult edges = candidateFinder.Find(shortMap, shortMap.Goal, 0.5);
        Check(edges.Success && edges.BasePathSteps == 6,
            $"candidates: 7 cells count 6 edges ({edges.BasePathSteps})");
        Check(edges.Success && edges.Radius == 3,
            $"candidates: floor(6 * 0.5) is 3 ({edges.Radius})");

        Check(RadiusCandidateFinder.IsOnCircle(new Cell(5, 5), new Cell(8, 9), 5),
            "candidates: offset (3,4) lies on radius 5");
        Check(RadiusCandidateFinder.IsOnCircle(new Cell(5, 5), new Cell(10, 5), 5),
            "candidates: offset (5,0) lies on radius 5");
        Check(!RadiusCandidateFinder.IsOnCircle(new Cell(5, 5), new Cell(9, 9), 5),
            "candidates: offset (4,4) is not on radius 5");
        Check(!RadiusCandidateFinder.IsOnCircle(new Cell(5, 5), new Cell(10, 6), 5),
            "candidates: offset (5,1) is not on radius 5");

        var ringMap = new GridMap(11, 11) { Start = new Cell(0, 0), Goal = new Cell(5, 5) };
        CandidateTargetsResult ring = candidateFinder.Find(ringMap, ringMap.Goal, 0.5);
        Check(ring.Success && ring.BasePathSteps == 10 && ring.Radius == 5,
            $"candidates: open map gives L=10, R=5 ({ring.BasePathSteps}/{ring.Radius})");
        Check(ring.CandidateCount == 12,
            $"candidates: all 12 circle cells returned ({ring.CandidateCount})");
        Check(MatchesRadiusCandidates(ringMap, ringMap.Goal, 0.5, ring),
            "candidates: independent oracle agrees on L, R and every cell");

        GridMap blockedMap = ringMap.Clone();
        blockedMap.SetCost(new Cell(8, 9), GridMap.Wall);
        CandidateTargetsResult blocked = candidateFinder.Find(blockedMap, blockedMap.Goal, 0.5);
        Check(blocked.Success && blocked.BasePathSteps == 10,
            "candidates: a wall away from the shortest path leaves L unchanged");
        Check(blocked.CandidateCount == 11 && !blocked.Candidates.Contains(new Cell(8, 9)),
            "candidates: a walled circle cell is dropped");

        GridMap cutOffMap = ringMap.Clone();
        foreach (Cell wall in new[] { new Cell(9, 5), new Cell(10, 4), new Cell(10, 6) })
            cutOffMap.SetCost(wall, GridMap.Wall);

        CandidateTargetsResult cutOff = candidateFinder.Find(cutOffMap, cutOffMap.Goal, 0.5);
        Check(cutOff.Success && !cutOff.Candidates.Contains(new Cell(10, 5)),
            "candidates: a traversable but sealed circle cell is dropped");
        Check(cutOff.CandidateCount == ring.CandidateCount - 1,
            $"candidates: exactly one unreachable cell removed ({cutOff.CandidateCount})");

        CandidateTargetsResult noRing = candidateFinder.Find(ringMap, ringMap.Goal, 10);
        Check(noRing.Success && noRing.Radius == 100 && noRing.CandidateCount == 0
              && noRing.Error == CandidateTargetError.None,
            $"candidates: a circle outside the map succeeds with 0 candidates ({noRing.CandidateCount})");

        GridMap pricedMap = corridorMap.Clone();
        pricedMap.SetCost(new Cell(6, 0), 9);
        CandidateTargetsResult priced = candidateFinder.Find(pricedMap, pricedMap.Goal, 0.5);
        Check(priced.Success && priced.BasePathSteps == 12
              && priced.Candidates.Contains(new Cell(6, 0)),
            "candidates: a price-9 cell keeps L and stays a candidate");

        GridMap walledCorridor = corridorMap.Clone();
        walledCorridor.SetCost(new Cell(3, 0), GridMap.Wall);
        CandidateTargetsResult brokenCorridor = candidateFinder.Find(walledCorridor, walledCorridor.Goal, 0.5);
        Check(!brokenCorridor.Success && brokenCorridor.Error == CandidateTargetError.Unreachable,
            "candidates: walling the corridor reports unreachable");

        var sameMap = new GridMap(5, 5) { Start = new Cell(2, 2), Goal = new Cell(2, 2) };
        CandidateTargetsResult zeroSteps = candidateFinder.Find(sameMap, sameMap.Goal, 0.5);
        Check(zeroSteps.Success && zeroSteps.BasePathSteps == 0 && zeroSteps.Radius == 0,
            $"candidates: spawn == entrance gives L=0, R=0 ({zeroSteps.Radius})");
        Check(zeroSteps.CandidateCount == 1 && zeroSteps.Candidates[0] == new Cell(2, 2),
            "candidates: radius 0 keeps only the entrance cell");

        CandidateTargetsResult zeroAlpha = candidateFinder.Find(ringMap, ringMap.Goal, 0);
        Check(zeroAlpha.Success && zeroAlpha.Radius == 0 && zeroAlpha.CandidateCount == 1
              && zeroAlpha.Candidates[0] == ringMap.Goal,
            "candidates: alpha 0 gives R=0 and is not forced to 1");

        CandidateTargetsResult nullMap = candidateFinder.Find(null!, new Cell(0, 0), 0.5);
        Check(!nullMap.Success && nullMap.Error == CandidateTargetError.NullMap,
            "candidates: null map rejected");

        CandidateTargetsResult outsideEntrance = candidateFinder.Find(ringMap, new Cell(99, 0), 0.5);
        Check(!outsideEntrance.Success && outsideEntrance.Error == CandidateTargetError.EntranceOutOfBounds
              && outsideEntrance.Note is not null && outsideEntrance.Note.Contains("边界"),
            "candidates: out-of-bounds entrance rejected with a reason");

        Check(candidateFinder.Find(ringMap, ringMap.Goal, -1).Error == CandidateTargetError.InvalidAlpha
              && candidateFinder.Find(ringMap, ringMap.Goal, double.NaN).Error == CandidateTargetError.InvalidAlpha
              && candidateFinder.Find(ringMap, ringMap.Goal, double.PositiveInfinity).Error == CandidateTargetError.InvalidAlpha,
            "candidates: negative, NaN and infinite ratios rejected");

        Check(candidateFinder.Find(ringMap, ringMap.Goal, double.MaxValue).Error == CandidateTargetError.RadiusOverflow,
            "candidates: an overflowing radius is reported");

        // ---- radius band: equal bounds stay a circle, different bounds become a ring ----
        CandidateTargetsResult ringBand = candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 3, 5);
        Check(ringBand.Success && ringBand.Radius == 5 && ringBand.MinRadius == 3
              && ringBand.MaxRadius == 5 && ringBand.IsRing,
            $"candidates: band 3-5 keeps the base radius and reports a ring ({ringBand.MinRadius}-{ringBand.MaxRadius})");
        Check(ringBand.CandidateCount > ring.CandidateCount,
            $"candidates: the ring holds more cells than its outer circle ({ringBand.CandidateCount} > {ring.CandidateCount})");
        Check(!ringBand.Candidates.Contains(new Cell(5, 6))
              && ringBand.Candidates.Contains(new Cell(5, 8)),
            "candidates: a distance-1 cell is outside the band while a distance-3 cell is inside");
        Check(MatchesRadiusCandidates(ringMap, ringMap.Goal, 0.5, ringBand),
            "candidates: ring cells match an independent band oracle");

        CandidateTargetsResult equalBand = candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 5, 5);
        Check(!equalBand.IsRing && equalBand.Candidates.SequenceEqual(ring.Candidates),
            "candidates: equal bounds reproduce the plain circle");

        CandidateTargetsResult wideBand = candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 0, 20);
        Check(wideBand.Success && wideBand.CandidateCount == 121,
            $"candidates: a band covering the map keeps every walkable cell ({wideBand.CandidateCount})");

        Check(candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 4, 2).Error
              == CandidateTargetError.InvalidRadiusRange,
            "candidates: a reversed radius range is rejected, not swapped");
        Check(candidateFinder.Find(ringMap, ringMap.Goal, 0.5, -1, 3).Error
              == CandidateTargetError.InvalidRadiusRange
              && candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 2, -4).Error
              == CandidateTargetError.InvalidRadiusRange,
            "candidates: negative radius bounds are rejected");

        CandidateTargetsResult halfBand = candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 3, null);
        Check(halfBand.Success && halfBand.MinRadius == 3 && halfBand.MaxRadius == 5
              && halfBand.Candidates.SequenceEqual(ringBand.Candidates),
            "candidates: a missing upper bound follows the base radius");

        Check(candidateFinder.Find(ringMap, ringMap.Goal, 0.5, null, 2).Error
              == CandidateTargetError.InvalidRadiusRange,
            "candidates: a missing lower bound that exceeds the upper bound is rejected");

        CandidateTargetsResult zeroBand = candidateFinder.Find(ringMap, ringMap.Goal, 0.5, 0, 0);
        Check(zeroBand.Success && zeroBand.CandidateCount == 1
              && zeroBand.Candidates[0] == ringMap.Goal,
            "candidates: a 0-0 band keeps only the entrance");

        // ---- obstacle-cluster constrained route ----
        var clusterRoute = new ObstacleClusterPathfinder();

        // A: one isolated wall marks exactly itself; the ring is its four open neighbours.
        GridMap single = BuildOpenMap(9, 9, new Cell(0, 0), new Cell(8, 8));
        single.SetCost(new Cell(4, 4), GridMap.Wall);
        Check(ObstacleCluster.Collect(single, new Cell(4, 4)).Count == 1,
            "cluster: an isolated wall is a one-cell cluster");
        List<Cell> singleRing = ObstacleCluster.Ring(single, ObstacleCluster.Collect(single, new Cell(4, 4)));
        Check(singleRing.Count == 4 && singleRing.Contains(new Cell(3, 4))
              && singleRing.Contains(new Cell(5, 4))
              && singleRing.Contains(new Cell(4, 3))
              && singleRing.Contains(new Cell(4, 5)),
            $"cluster: one wall has four open neighbours ({singleRing.Count})");

        // B: an L-shaped run of walls stays one cluster.
        GridMap run = single.Clone();
        run.SetCost(new Cell(3, 4), GridMap.Wall);
        run.SetCost(new Cell(3, 5), GridMap.Wall);
        Check(ObstacleCluster.Collect(run, new Cell(4, 4)).Count == 3,
            "cluster: orthogonally connected walls form one cluster");
        Check(ObstacleCluster.Ring(run, ObstacleCluster.Collect(run, new Cell(4, 4))).Count == 7,
            "cluster: the L-shaped ring holds seven cells");

        // C: diagonal contact is not a connection.
        GridMap diagonal = BuildOpenMap(9, 9, new Cell(0, 0), new Cell(8, 8));
        diagonal.SetCost(new Cell(4, 4), GridMap.Wall);
        diagonal.SetCost(new Cell(5, 5), GridMap.Wall);
        Check(ObstacleCluster.Collect(diagonal, new Cell(4, 4)).Count == 1,
            "cluster: diagonal walls stay separate clusters");

        // D: an open cell touching two cluster cells appears once.
        GridMap touching = BuildOpenMap(9, 9, new Cell(0, 0), new Cell(8, 8));
        foreach (Cell wall in new[] { new Cell(4, 4), new Cell(4, 5), new Cell(5, 4) })
            touching.SetCost(wall, GridMap.Wall);

        List<Cell> touchingRing = ObstacleCluster.Ring(touching, ObstacleCluster.Collect(touching, new Cell(4, 4)));
        Check(touchingRing.Count(cell => cell == new Cell(5, 5)) == 1,
            "cluster: a cell touching two cluster walls is listed once");
        Check(touchingRing.All(cell => !touching.IsWall(cell)),
            "cluster: the ring never contains cluster walls");

        // E: the plain route misses the ring, so the constrained route must bend.
        GridMap detour = BuildOpenMap(9, 9, new Cell(0, 0), new Cell(8, 0));
        detour.SetCost(new Cell(0, 5), GridMap.Wall);
        int plainCost = ReferenceCost(detour);
        clusterRoute.ReferencePoint = new Cell(0, 5);
        SearchResult detourRoute = clusterRoute.Search(detour);
        Check(detourRoute.Found && IsValidPath(detour, detourRoute.Path),
            "cluster route: the constrained path is a legal walk");
        Check(detourRoute.Path.Any(cell => clusterRoute.RingCells.Contains(cell)),
            "cluster route: the path touches at least one ring cell");
        var oracleDetour = ConstrainedOracle(detour, new Cell(0, 5));
        Check(detourRoute.Cost == oracleDetour.Cost,
            $"cluster route: cost {detourRoute.Cost} matches the all-targets oracle ({oracleDetour.Cost})");
        Check(clusterRoute.ViaCell == oracleDetour.Via,
            "cluster route: the chosen ring cell matches the oracle");
        Check(detourRoute.Cost > plainCost,
            $"cluster route: an off-route cluster really costs extra ({detourRoute.Cost} > {plainCost})");

        // F: a cluster sitting on the direct route adds no cost.
        GridMap onRoute = BuildOpenMap(9, 9, new Cell(0, 4), new Cell(8, 4));
        onRoute.SetCost(new Cell(4, 4), GridMap.Wall);
        onRoute.SetCost(new Cell(4, 5), GridMap.Wall);
        int routePlain = ReferenceCost(onRoute);
        clusterRoute.ReferencePoint = new Cell(4, 4);
        SearchResult routeRoute = clusterRoute.Search(onRoute);
        Check(routeRoute.Found && routeRoute.Cost == routePlain,
            $"cluster route: a cluster on the bridge costs the same ({routeRoute.Cost} == {routePlain})");

        // G: a pricey ring cell is avoided in favour of the cheap side.
        GridMap pricedCluster = BuildOpenMap(9, 9, new Cell(0, 4), new Cell(8, 4));
        pricedCluster.SetCost(new Cell(4, 4), GridMap.Wall);
        pricedCluster.SetCost(new Cell(3, 4), 9);
        pricedCluster.SetCost(new Cell(4, 3), 9);
        pricedCluster.SetCost(new Cell(4, 5), 9);
        clusterRoute.ReferencePoint = new Cell(4, 4);
        SearchResult pricedRoute = clusterRoute.Search(pricedCluster);
        var oraclePriced = ConstrainedOracle(pricedCluster, new Cell(4, 4));
        Check(pricedRoute.Found && pricedRoute.Cost == oraclePriced.Cost,
            $"cluster route: weighted prices pick the cheapest ring cell ({pricedRoute.Cost} vs {oraclePriced.Cost})");
        Check(clusterRoute.ViaCell == new Cell(5, 4),
            $"cluster route: price-9 neighbours lose to the open one ({clusterRoute.ViaCell})");

        // H: a cluster sealed inside a room has a ring, but none of it connects both ends.
        GridMap sealedRoom = BuildOpenMap(9, 9, new Cell(0, 0), new Cell(8, 8));
        for (int index = 2; index <= 6; index++)
        {
            sealedRoom.SetCost(new Cell(index, 2), GridMap.Wall);
            sealedRoom.SetCost(new Cell(index, 6), GridMap.Wall);
            sealedRoom.SetCost(new Cell(2, index), GridMap.Wall);
            sealedRoom.SetCost(new Cell(6, index), GridMap.Wall);
        }

        sealedRoom.SetCost(new Cell(4, 4), GridMap.Wall);
        clusterRoute.ReferencePoint = new Cell(4, 4);
        SearchResult sealedClusterRoute = clusterRoute.Search(sealedRoom);
        Check(!sealedClusterRoute.Found && sealedClusterRoute.Note is not null
              && sealedClusterRoute.Note.Contains("无法同时连接"),
            "cluster route: an isolated ring reports the constraint as unsatisfiable");
        Check(clusterRoute.RingCells.Count == 4,
            $"cluster route: the sealed cluster still reports its ring ({clusterRoute.RingCells.Count})");

        // I: a cluster on the border ignores out-of-map neighbours.
        GridMap corner = BuildOpenMap(9, 9, new Cell(0, 8), new Cell(8, 8));
        corner.SetCost(new Cell(0, 0), GridMap.Wall);
        clusterRoute.ReferencePoint = new Cell(0, 0);
        SearchResult cornerRoute = clusterRoute.Search(corner);
        Check(cornerRoute.Found && clusterRoute.RingCells.Count == 2,
            $"cluster route: a corner cluster keeps two in-map neighbours ({clusterRoute.RingCells.Count})");
        var oracleCorner = ConstrainedOracle(corner, new Cell(0, 0));
        Check(cornerRoute.Cost == oracleCorner.Cost,
            $"cluster route: the corner case still matches the oracle ({cornerRoute.Cost} vs {oracleCorner.Cost})");

        // J: weighted map, constraint on top of prices.
        GridMap weights = BuildOpenMap(7, 7, new Cell(0, 3), new Cell(6, 3));
        weights.SetCost(new Cell(3, 3), GridMap.Wall);
        foreach (Cell cell in new[] { new Cell(2, 2), new Cell(3, 2), new Cell(4, 2) })
            weights.SetCost(cell, 9);

        clusterRoute.ReferencePoint = new Cell(3, 3);
        SearchResult weightedRoute = clusterRoute.Search(weights);
        var oracleWeights = ConstrainedOracle(weights, new Cell(3, 3));
        Check(weightedRoute.Found && weightedRoute.Cost == oracleWeights.Cost,
            $"cluster route: weighted route cost {weightedRoute.Cost} matches the oracle ({oracleWeights.Cost})");
        Check(weightedRoute.Cost > 6,
            "cluster route: the weighted route really pays the extra price");

        // K: the same instance is stateless between calls, so edits are always re-read.
        GridMap editedMap = BuildOpenMap(8, 8, new Cell(0, 0), new Cell(7, 7));
        editedMap.SetCost(new Cell(5, 4), GridMap.Wall);
        clusterRoute.ReferencePoint = new Cell(5, 4);
        SearchResult before = clusterRoute.Search(editedMap);
        int beforeRing = clusterRoute.RingCells.Count;

        editedMap.SetCost(new Cell(4, 4), GridMap.Wall);
        SearchResult after = clusterRoute.Search(editedMap);
        int afterRing = clusterRoute.RingCells.Count;

        Check(before.Found, "cluster route: dynamic map plans before the edit");
        Check(after.Found && afterRing != beforeRing,
            $"cluster route: the second call re-reads the edited map ({beforeRing} -> {afterRing})");
        Check(afterRing == ObstacleCluster.Ring(editedMap, ObstacleCluster.Collect(editedMap, new Cell(5, 4))).Count,
            "cluster route: the ring after the edit matches a fresh scan");

        // Reference and endpoint errors never fall back to the plain route.
        clusterRoute.ReferencePoint = new Cell(99, 0);
        Check(!clusterRoute.Search(BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4))).Found
              && clusterRoute.RingCells.Count == 0,
            "cluster route: an out-of-bounds reference fails");

        GridMap openMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        clusterRoute.ReferencePoint = new Cell(2, 2);
        SearchResult notWall = clusterRoute.Search(openMap);
        Check(!notWall.Found && notWall.Note is not null && notWall.Note.Contains("不是障碍"),
            "cluster route: an open reference cell is rejected, not replaced");

        clusterRoute.ReferencePoint = null;
        Check(!clusterRoute.Search(openMap).Found,
            "cluster route: a missing reference fails instead of guessing");

        GridMap walledGoal = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        walledGoal.SetCost(new Cell(3, 3), GridMap.Wall);
        walledGoal.Goal = new Cell(3, 3);
        clusterRoute.ReferencePoint = new Cell(3, 3);
        SearchResult badEnds = clusterRoute.Search(walledGoal);
        Check(!badEnds.Found && badEnds.Note is not null && badEnds.Note.Contains("不可通行"),
            "cluster route: an unwalkable entrance fails");

        // Compare mode must not share mutable state between two module instances.
        var moduleA = new ObstacleClusterPathfinder();
        var moduleB = new ObstacleClusterPathfinder();
        moduleA.ReferencePoint = new Cell(4, 4);
        moduleB.ReferencePoint = new Cell(3, 4);
        SearchResult resultA = moduleA.Search(single);
        SearchResult resultB = moduleB.Search(run);
        Check(moduleA.UsedReference == new Cell(4, 4) && moduleB.UsedReference == new Cell(3, 4)
              && moduleA.RingCells.Count == 4 && moduleB.RingCells.Count == 7,
            "cluster route: instances keep their own reference and ring");

        // ---- 06: maximum weighted perpendicular distance route (simple walks only) ----
        // The spec's own arithmetic: Q = (5,5), four horizontal moves on row 2, three
        // vertical moves on column 9.
        var specRow = new List<Cell> { new(0, 2), new(1, 2), new(2, 2), new(3, 2), new(4, 2) };
        Check(MaxPerpendicularPathfinder.CalculatePathScore(specRow, new Cell(5, 5)) == 12,
            "perpendicular: four horizontal moves on row 2 score 4 * |2 - 5| = 12");
        var specColumn = new List<Cell> { new(9, 0), new(9, 1), new(9, 2), new(9, 3) };
        Check(MaxPerpendicularPathfinder.CalculatePathScore(specColumn, new Cell(5, 5)) == 12,
            "perpendicular: three vertical moves on column 9 score 3 * |9 - 5| = 12");

        var specElbow = new List<Cell> { new(9, 0), new(9, 1), new(9, 2), new(8, 2) };
        Check(MaxPerpendicularPathfinder.TurnCount(specRow) == 0,
            "perpendicular: a straight run has no turns");
        Check(MaxPerpendicularPathfinder.TurnCount(specElbow) == 1,
            "perpendicular: one heading change counts one turn");
        Check(Math.Abs(MaxPerpendicularPathfinder.AverageEdgeWeight(12, 4) - 3) < 1e-9,
            "perpendicular: average edge weight is score / steps");
        Check(MaxPerpendicularPathfinder.AverageEdgeWeight(0, 0) == 0,
            "perpendicular: a zero-step route averages 0 without dividing by zero");

        // Exact tests raise the budget so small maps finish the whole simple-walk space.
        var scoreRoute = new MaxPerpendicularPathfinder { MaxExpandedNodes = 3_000_000, MaxMilliseconds = 5_000 };

        // A: marker in the middle of the map.
        GridMap markerCentre = BuildOpenMap(5, 4, new Cell(0, 1), new Cell(4, 1));
        scoreRoute.ReferencePoint = new Cell(2, 1);
        SearchResult centreRoute = scoreRoute.Search(markerCentre);
        CheckScoreRoute(markerCentre, new Cell(2, 1), centreRoute, "A centre");
        long centreOracle = PerpendicularOracle(markerCentre, new Cell(2, 1));
        Console.WriteLine($"  centre route: {centreRoute.Path.Count - 1} steps, score {centreRoute.Score}, "
                          + $"nodes {centreRoute.Explored.Count}, {centreRoute.ElapsedMs:F1} ms, oracle {centreOracle}");
        Check(centreRoute.Score == centreOracle,
            $"perpendicular A: centre score {centreRoute.Score} matches the oracle ({centreOracle})");
        Check(centreRoute.IsOptimal && centreRoute.TerminationReason!.Contains("精确"),
            "perpendicular A: a finished exact search reports the optimum");
        Check(centreRoute.Score > ShortestScore(markerCentre, new Cell(2, 1)),
            "perpendicular A: the centre route beats the plain shortest route");
        Check(centreRoute.Path.Any(cell => cell.Y == 0 || cell.Y == 3),
            "perpendicular A: the route uses a far row");

        // B: marker on the map border.
        GridMap markerEdge = BuildOpenMap(5, 4, new Cell(0, 0), new Cell(4, 0));
        scoreRoute.ReferencePoint = new Cell(0, 0);
        SearchResult edgeRoute = scoreRoute.Search(markerEdge);
        CheckScoreRoute(markerEdge, new Cell(0, 0), edgeRoute, "B edge");
        long edgeOracle = PerpendicularOracle(markerEdge, new Cell(0, 0));
        Check(edgeRoute.Score == edgeOracle,
            $"perpendicular B: edge score {edgeRoute.Score} matches the oracle ({edgeOracle})");
        Check(edgeRoute.Score > 0 && edgeRoute.Path.Count - 1 > 4,
            "perpendicular B: the far side is worth extra steps");

        // C: marker on the start gate and on the goal gate.
        GridMap markerGates = BuildOpenMap(3, 3, new Cell(0, 0), new Cell(2, 2));
        scoreRoute.ReferencePoint = new Cell(0, 0);
        SearchResult startMarkerRoute = scoreRoute.Search(markerGates);
        CheckScoreRoute(markerGates, new Cell(0, 0), startMarkerRoute, "C start marker");
        Check(startMarkerRoute.Score == PerpendicularOracle(markerGates, new Cell(0, 0)),
            "perpendicular C: marker on the start gate scores exactly");

        scoreRoute.ReferencePoint = new Cell(2, 2);
        SearchResult goalMarkerRoute = scoreRoute.Search(markerGates);
        CheckScoreRoute(markerGates, new Cell(2, 2), goalMarkerRoute, "C goal marker");
        Check(goalMarkerRoute.Score == PerpendicularOracle(markerGates, new Cell(2, 2)),
            "perpendicular C: marker on the goal gate scores exactly");

        // D: no obstacles at all.
        GridMap openSquare = BuildOpenMap(4, 4, new Cell(0, 0), new Cell(3, 3));
        scoreRoute.ReferencePoint = new Cell(1, 1);
        SearchResult openSquareRoute = scoreRoute.Search(openSquare);
        CheckScoreRoute(openSquare, new Cell(1, 1), openSquareRoute, "D open map");
        Check(openSquareRoute.Score == PerpendicularOracle(openSquare, new Cell(1, 1)),
            $"perpendicular D: open-map score {openSquareRoute.Score} matches the oracle");

        // E: natural obstacles around the marker.
        GridMap walledSquare = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        foreach (Cell wall in new[]
                 {
                     new Cell(1, 1), new Cell(1, 2), new Cell(1, 3),
                     new Cell(3, 1), new Cell(3, 2), new Cell(3, 3)
                 })
            walledSquare.SetCost(wall, GridMap.Wall);

        scoreRoute.ReferencePoint = new Cell(2, 2);
        SearchResult walledSquareRoute = scoreRoute.Search(walledSquare);
        CheckScoreRoute(walledSquare, new Cell(2, 2), walledSquareRoute, "E walls");
        Check(walledSquareRoute.Score == PerpendicularOracle(walledSquare, new Cell(2, 2)),
            $"perpendicular E: walled-map score {walledSquareRoute.Score} matches the oracle");
        Check(walledSquareRoute.Path.Any(cell => cell.Y == 4 || cell.Y == 0),
            "perpendicular E: the route still reaches the far rows around the walls");

        // F: a single narrow corridor leaves exactly one route.
        var corridor = new GridMap(2, 6) { Start = new Cell(1, 0), Goal = new Cell(1, 5) };
        for (int y = 1; y <= 5; y++)
            corridor.SetCost(new Cell(0, y), GridMap.Wall);

        scoreRoute.ReferencePoint = new Cell(0, 0);
        SearchResult corridorRoute = scoreRoute.Search(corridor);
        CheckScoreRoute(corridor, new Cell(0, 0), corridorRoute, "F corridor");
        Check(corridorRoute.Path.Count - 1 == 5 && corridorRoute.Score == 5,
            $"perpendicular F: the only corridor scores 5 over 5 steps ({corridorRoute.Score})");
        Check(corridorRoute.IsOptimal, "perpendicular F: a forced route is trivially optimal");

        // G: a 2x7 strip where the shortest route scores 0 and a detour scores more.
        var ladder = new GridMap(7, 3) { Start = new Cell(0, 1), Goal = new Cell(6, 1) };
        for (int x = 0; x < ladder.Width; x++)
            ladder.SetCost(new Cell(x, 2), GridMap.Wall);

        scoreRoute.ReferencePoint = new Cell(3, 1);
        SearchResult ladderRoute = scoreRoute.Search(ladder);
        CheckScoreRoute(ladder, new Cell(3, 1), ladderRoute, "G detour");
        int ladderPlainSteps = ReferenceSteps(ladder);
        long ladderOracle = PerpendicularOracle(ladder, new Cell(3, 1));
        Console.WriteLine($"  ladder route: {ladderRoute.Path.Count - 1} steps, score {ladderRoute.Score}, "
                          + $"plain {ladderPlainSteps} steps, oracle {ladderOracle}");
        Check(ladderRoute.Score == ladderOracle,
            $"perpendicular G: detour score {ladderRoute.Score} matches the oracle ({ladderOracle})");
        Check(ladderRoute.Score > 0 && ladderRoute.Path.Count - 1 > ladderPlainSteps,
            "perpendicular G: the highest score is not the shortest route");
        Check(ShortestScore(ladder, new Cell(3, 1)) == 0,
            "perpendicular G: the plain route along the marker row scores 0");

        // H: the optimum is a simple walk, so it cannot farm a high-weight loop.
        Check(ladderRoute.Path.Distinct().Count() == ladderRoute.Path.Count,
            "perpendicular H: the optimum never repeats a cell");
        Check(ladderRoute.Score == MaxPerpendicularPathfinder.CalculatePathScore(ladderRoute.Path, new Cell(3, 1)),
            "perpendicular H: score comes from the final path only, not from a loop");

        // I: start == goal, zero moves.
        var singleScore = new GridMap(3, 3) { Start = new Cell(1, 1), Goal = new Cell(1, 1) };
        scoreRoute.ReferencePoint = new Cell(0, 0);
        SearchResult singleScoreRoute = scoreRoute.Search(singleScore);
        Check(singleScoreRoute.Found && singleScoreRoute.Path.Count == 1,
            "perpendicular I: a one-cell route is returned when start equals goal");
        Check(singleScoreRoute.Score == 0 && singleScoreRoute.IsOptimal,
            "perpendicular I: zero moves score 0 and count as optimal");
        Check(ShortestScore(singleScore, new Cell(0, 0)) == 0,
            "perpendicular I: score 0 needs no average-weight division");

        // J: the goal is walled off, so there is no legal route at all.
        var deadEnds = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        for (int y = 0; y < deadEnds.Height; y++)
            deadEnds.SetCost(new Cell(2, y), GridMap.Wall);

        scoreRoute.ReferencePoint = new Cell(2, 2);
        SearchResult noScoreRoute = scoreRoute.Search(deadEnds);
        Check(!noScoreRoute.Found && noScoreRoute.Note is not null
              && noScoreRoute.Note.Contains("不存在合法路径"),
            "perpendicular J: a cut-off goal reports no legal route");
        Check(noScoreRoute.TerminationReason is not null
              && noScoreRoute.TerminationReason.Contains("不存在"),
            "perpendicular J: the termination reason says the route does not exist");

        // K: the node budget stops the exact search and never claims optimality.
        var limitedFinder = new MaxPerpendicularPathfinder
        {
            MaxExpandedNodes = 60,
            MaxMilliseconds = 1_000_000
        };
        limitedFinder.ReferencePoint = new Cell(3, 1);
        SearchResult limitedRoute = limitedFinder.Search(ladder);
        CheckScoreRoute(ladder, new Cell(3, 1), limitedRoute, "K node limit");
        Check(!limitedRoute.IsOptimal && limitedRoute.TerminationReason!.Contains("节点上限"),
            $"perpendicular K: the node cap is reported ({limitedRoute.TerminationReason})");
        Check(limitedRoute.Score >= ShortestScore(ladder, new Cell(3, 1)),
            "perpendicular K: a bounded search keeps the seed route instead of inventing one");

        // L: beam search is explicitly approximate.
        var beamFinder = new MaxPerpendicularPathfinder
        {
            Mode = ScoreSearchMode.Beam,
            BeamWidth = 16,
            MaxExpandedNodes = 200_000,
            MaxMilliseconds = 2_000
        };
        beamFinder.ReferencePoint = new Cell(3, 1);
        SearchResult beamRoute = beamFinder.Search(ladder);
        CheckScoreRoute(ladder, new Cell(3, 1), beamRoute, "L beam");
        Check(!beamRoute.IsOptimal && beamRoute.TerminationReason!.Contains("近似"),
            $"perpendicular L: beam search never claims optimality ({beamRoute.TerminationReason})");
        Check(beamRoute.Score >= ShortestScore(ladder, new Cell(3, 1)),
            "perpendicular L: beam search starts from the seed route, not from nothing");

        // M: marker and gate validation; a wall marker is legal.
        scoreRoute.ReferencePoint = new Cell(99, 0);
        SearchResult markerOutside = scoreRoute.Search(ladder);
        Check(!markerOutside.Found && markerOutside.Note is not null
              && markerOutside.Note.Contains("标记点"),
            "perpendicular M: an out-of-bounds marker fails");

        scoreRoute.ReferencePoint = null;
        SearchResult markerDefault = scoreRoute.Search(ladder);
        Check(markerDefault.Found && scoreRoute.UsedReference == ladder.Goal,
            "perpendicular M: a missing marker falls back to the goal gate");

        scoreRoute.ReferencePoint = new Cell(2, 2);
        SearchResult markerWall = scoreRoute.Search(walledSquare);
        CheckScoreRoute(walledSquare, new Cell(2, 2), markerWall, "M wall marker");

        // The goal must be walled before it becomes the goal: GridMap protects the gates.
        GridMap walledGate = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        walledGate.SetCost(new Cell(2, 2), GridMap.Wall);
        walledGate.Goal = new Cell(2, 2);
        scoreRoute.ReferencePoint = new Cell(0, 0);
        SearchResult walledGateRoute = scoreRoute.Search(walledGate);
        Check(!walledGateRoute.Found && walledGateRoute.Note is not null
              && walledGateRoute.Note.Contains("不可通行"),
            "perpendicular M: an unwalkable gate fails");

        // N: same instance, same map, same answer; two instances keep their own marker.
        scoreRoute.ReferencePoint = new Cell(2, 1);
        SearchResult repeatCentre = scoreRoute.Search(markerCentre);
        Check(repeatCentre.Score == centreRoute.Score && SamePath(repeatCentre.Path, centreRoute.Path),
            "perpendicular N: repeated calls return the same score and route");

        var markerA = new MaxPerpendicularPathfinder { MaxExpandedNodes = 3_000_000, MaxMilliseconds = 5_000 };
        var markerB = new MaxPerpendicularPathfinder { MaxExpandedNodes = 3_000_000, MaxMilliseconds = 5_000 };
        markerA.ReferencePoint = new Cell(0, 0);
        markerB.ReferencePoint = new Cell(4, 3);
        SearchResult resultMarkerA = markerA.Search(markerCentre);
        SearchResult resultMarkerB = markerB.Search(markerCentre);
        Check(markerA.UsedReference == new Cell(0, 0) && markerB.UsedReference == new Cell(4, 3),
            "perpendicular N: instances keep their own marker");
        Check(resultMarkerA.Score != resultMarkerB.Score,
            "perpendicular N: a different marker changes the score");

        // ---- 07 · minimum reference traversal: crossings first, moves second ----
        var plainOpen = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));

        // A: no references at all -> the plain shortest route.
        SearchResult noReferenceRoute = ReferenceRoute(plainOpen, null, "A no references");
        (int Crossings, int Length) noReferenceOracle = ReferenceTraversalOracle(plainOpen, null);
        Check(noReferenceRoute.Path.Count - 1 == 8,
            $"reference A: open 5x5 needs 8 moves ({noReferenceRoute.Path.Count - 1})");
        Check(noReferenceRoute.ReferenceCount == 0, "reference A: no crossings without references");
        Check(noReferenceRoute.Path.Count - 1
              == new ShortestStepsPathfinder().Search(plainOpen).Path.Count - 1,
            "reference A: same length as the plain shortest module");
        Check((noReferenceRoute.ReferenceCount, noReferenceRoute.Path.Count - 1) == noReferenceOracle,
            "reference A: matches the exhaustive oracle");

        // B: references exist but the shortest staircase can step around them.
        Cell[] avoidableRefs = { new(2, 2), new(1, 3) };
        SearchResult avoidableRoute = ReferenceRoute(plainOpen, avoidableRefs, "B avoidable references");
        Check(avoidableRoute.ReferenceCount == 0, "reference B: the route avoids both references");
        Check(avoidableRoute.Path.Count - 1 == 8, "reference B: avoiding them costs no extra move");
        Check((avoidableRoute.ReferenceCount, avoidableRoute.Path.Count - 1)
              == ReferenceTraversalOracle(plainOpen, avoidableRefs),
            "reference B: matches the exhaustive oracle");

        // C: a reference sits on the direct row, a longer detour exists around it.
        var detourMap = new GridMap(5, 3) { Start = new Cell(0, 1), Goal = new Cell(4, 1) };
        Cell[] detourRefs = { new(2, 1) };
        SearchResult referenceDetourRoute = ReferenceRoute(detourMap, detourRefs, "C detour");
        int detourShortest = new ShortestStepsPathfinder().Search(detourMap).Path.Count - 1;
        Check(referenceDetourRoute.ReferenceCount == 0, "reference C: refuses the reference on the direct row");
        Check(referenceDetourRoute.Path.Count - 1 == 6 && referenceDetourRoute.Path.Count - 1 > detourShortest,
            $"reference C: takes 6 moves instead of the {detourShortest}-move direct route");
        Check((referenceDetourRoute.ReferenceCount, referenceDetourRoute.Path.Count - 1)
              == ReferenceTraversalOracle(detourMap, detourRefs),
            "reference C: matches the exhaustive oracle");

        // D: a wall band with a single door, and that door is a reference.
        var bandMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        for (int x = 0; x < 5; x++) bandMap.SetCost(new Cell(x, 2), GridMap.Wall);
        bandMap.SetCost(new Cell(2, 2), GridMap.MinCost);
        Cell[] doorRefs = { new(2, 2) };
        SearchResult bandRoute = ReferenceRoute(bandMap, doorRefs, "D only door");
        Check(bandRoute.ReferenceCount == 1, "reference D: the only door must be crossed once");
        Check(bandRoute.Path.Contains(new Cell(2, 2)), "reference D: the route uses the reference door");
        Check((bandRoute.ReferenceCount, bandRoute.Path.Count - 1)
              == ReferenceTraversalOracle(bandMap, doorRefs),
            "reference D: matches the exhaustive oracle");

        // E: two references both sit on the only two ways out of the start gate.
        var twoDoorMap = new GridMap(5, 3) { Start = new Cell(0, 1), Goal = new Cell(4, 1) };
        twoDoorMap.SetCost(new Cell(0, 0), GridMap.Wall);
        twoDoorMap.SetCost(new Cell(0, 2), GridMap.Wall);
        Cell[] twoDoorRefs = { new(1, 1), new(3, 1) };
        SearchResult twoDoorRoute = ReferenceRoute(twoDoorMap, twoDoorRefs, "E two references");
        Check(twoDoorRoute.ReferenceCount == 1,
            "reference E: crosses exactly one of the two references");
        Check((twoDoorRoute.ReferenceCount, twoDoorRoute.Path.Count - 1)
              == ReferenceTraversalOracle(twoDoorMap, twoDoorRefs),
            "reference E: matches the exhaustive oracle");

        // G: 4 moves crossing both references loses to 6 moves crossing one.
        Check(twoDoorRoute.Path.Count - 1 == 6,
            $"reference G: prefers one crossing in 6 moves over two crossings in 4 ({twoDoorRoute.Path.Count - 1})");

        // F: both doors cost one crossing; the straight 3-move door has to beat the
        // 5-move door that detours through the row below.
        var tieMap = new GridMap(4, 3) { Start = new Cell(0, 1), Goal = new Cell(3, 1) };
        tieMap.SetCost(new Cell(2, 0), GridMap.Wall);
        Cell[] tieRefs = { new(2, 1), new(2, 2) };
        SearchResult tieRoute = ReferenceRoute(tieMap, tieRefs, "F tie on crossings");
        Check(tieRoute.ReferenceCount == 1, "reference F: both doors cost one crossing");
        Check(tieRoute.Path.Count - 1 == 3 && tieRoute.Path.Contains(new Cell(2, 1))
              && !tieRoute.Path.Contains(new Cell(2, 2)),
            $"reference F: keeps the 3-move door over the 5-move one ({tieRoute.Path.Count - 1})");
        Check((tieRoute.ReferenceCount, tieRoute.Path.Count - 1)
              == ReferenceTraversalOracle(tieMap, tieRefs),
            "reference F: matches the exhaustive oracle");

        // H / I: the gates are fixed positions, never counted as crossings.
        SearchResult startReference = ReferenceRoute(plainOpen, new[] { new Cell(0, 0) }, "H start reference");
        Check(startReference.ReferenceCount == 0 && startReference.Path.Count - 1 == 8,
            "reference H: the start gate never counts as a crossing");

        SearchResult goalReference = ReferenceRoute(plainOpen, new[] { new Cell(4, 4) }, "I goal reference");
        Check(goalReference.ReferenceCount == 0 && goalReference.Path.Count - 1 == 8,
            "reference I: the goal gate never counts as a crossing");

        // J: the same coordinate listed three times still counts once.
        var duplicateModule = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = new[] { new Cell(2, 2), new Cell(2, 2), new Cell(2, 2) }
        };
        SearchResult duplicateRoute = duplicateModule.Search(bandMap);
        Check(duplicateRoute.Found && duplicateRoute.ReferenceCount == 1,
            "reference J: repeated coordinates count once");
        Check(duplicateModule.UsedReferenceCells.Count == 1,
            "reference J: repeated coordinates are de-duplicated");
        Check(MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(
                  duplicateRoute, bandMap, duplicateModule.ReferenceCells) is null,
            "reference J: validation agrees with the de-duplicated set");

        // K: a reference painted on a wall stays impassable, so it is never a crossing.
        var wallReferenceMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        wallReferenceMap.SetCost(new Cell(1, 1), GridMap.Wall);
        Cell[] wallAndOpenRefs = { new(1, 1), new(2, 2) };
        SearchResult wallReferenceRoute = ReferenceRoute(wallReferenceMap, wallAndOpenRefs, "K wall reference");
        Check(wallReferenceRoute.ReferenceCount == 0,
            "reference K: a reference on a wall is never entered");
        Check(!wallReferenceRoute.Path.Contains(new Cell(1, 1)),
            "reference K: the walled reference stays impassable");

        // L: walls cut the map in two; references must not be used as doors.
        var cutMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        for (int y = 0; y < 5; y++) cutMap.SetCost(new Cell(2, y), GridMap.Wall);
        SearchResult cutRoute = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = new[] { new Cell(2, 2) }
        }.Search(cutMap);
        Check(!cutRoute.Found, "reference L: a walled-off map has no route");
        Check(cutRoute.Note == "不存在合法路径" && cutRoute.TerminationReason == "不存在合法路径",
            "reference L: reports the missing route instead of forging one");
        Check(!cutRoute.IsOptimal, "reference L: a failed search never claims optimality");

        // M: start equals goal.
        var sameGate = BuildOpenMap(3, 3, new Cell(1, 1), new Cell(1, 1));
        SearchResult sameGateRoute = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = new[] { new Cell(0, 0) }
        }.Search(sameGate);
        Check(sameGateRoute.Found && sameGateRoute.Path.Count == 1
              && sameGateRoute.ReferenceCount == 0 && sameGateRoute.IsOptimal,
            "reference M: start equals goal yields a zero-move optimal route");

        // Input checks.
        var badReference = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = new[] { new Cell(9, 9) }
        };
        SearchResult badReferenceResult = badReference.Search(plainOpen);
        Check(!badReferenceResult.Found && badReferenceResult.Note is not null
              && badReferenceResult.Note.Contains("参考点")
              && badReferenceResult.TerminationReason == "输入无效",
            "reference input: an out-of-bounds reference fails");

        GridMap referenceWalledGoal = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        referenceWalledGoal.SetCost(new Cell(2, 2), GridMap.Wall);
        referenceWalledGoal.Goal = new Cell(2, 2);
        SearchResult referenceWalledGoalResult = new MinimumReferenceTraversalPathfinder().Search(referenceWalledGoal);
        Check(!referenceWalledGoalResult.Found && referenceWalledGoalResult.Note is not null
              && referenceWalledGoalResult.Note.Contains("终点不可通行"),
            "reference input: a walled goal is rejected");

        SearchResult nullMapResult = new MinimumReferenceTraversalPathfinder().Search(null!);
        Check(!nullMapResult.Found && nullMapResult.Note == "地图为空",
            "reference input: a null map is rejected");

        // The independent validator must notice a wrong crossing count.
        var forged = new SearchResult
        {
            Found = true,
            Path = new List<Cell>(bandRoute.Path),
            ReferenceCount = 0
        };
        Check(MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(forged, bandMap, doorRefs) is not null,
            "reference validation: a wrong crossing count is caught");

        // Repeated calls stay deterministic.
        var repeatModule = new MinimumReferenceTraversalPathfinder { ReferenceCells = twoDoorRefs };
        SearchResult repeatFirst = repeatModule.Search(twoDoorMap);
        SearchResult repeatSecond = repeatModule.Search(twoDoorMap);
        Check(SamePath(repeatFirst.Path, repeatSecond.Path)
              && repeatFirst.ReferenceCount == repeatSecond.ReferenceCount,
            "reference N: repeated calls return the same route");

        // ---- portal facility: chaotic all-to-all teleports shared by every probe ----

        // A: no portal at all -> the probe must return the plain answers unchanged.
        var emptyFacility = new PortalFacility();
        GridMap portalEmptyMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        SearchResult emptyProbeSteps = new ShortestStepsPathfinder
        {
            Probe = emptyFacility.CreateProbe(portalEmptyMap)
        }.Search(portalEmptyMap);
        SearchResult emptyProbeCost = new MinCostPathfinder
        {
            Probe = emptyFacility.CreateProbe(portalEmptyMap)
        }.Search(portalEmptyMap);
        SearchResult plainStepWalk = new ShortestStepsPathfinder().Search(portalEmptyMap);
        SearchResult plainPriceWalk = new MinCostPathfinder().Search(portalEmptyMap);

        Check(SamePath(emptyProbeSteps.Path, plainStepWalk.Path) && emptyProbeSteps.Cost == plainStepWalk.Cost,
            "portal A: an empty facility leaves the step route unchanged");
        Check(SamePath(emptyProbeCost.Path, plainPriceWalk.Path) && emptyProbeCost.Cost == plainPriceWalk.Cost,
            "portal A: an empty facility leaves the price route unchanged");

        // B: two portals far apart; the jump is the shorter way across.
        GridMap twoPortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var twoPortalFacility = new PortalFacility { TeleportCost = 0 };
        bool placedFirst = twoPortalFacility.Add(twoPortalMap, new Cell(1, 1), out string? firstNote);
        bool placedSecond = twoPortalFacility.Add(twoPortalMap, new Cell(3, 3), out string? secondNote);

        Check(placedFirst && placedSecond,
            $"portal B: two portals placed ({firstNote ?? "ok"} / {secondNote ?? "ok"})");
        Check(twoPortalMap.IsWall(new Cell(1, 1)) && twoPortalMap.IsWall(new Cell(3, 3)),
            "portal B: portal cells are no longer ordinary walkable cells");
        Check(twoPortalFacility.Kind(twoPortalMap, new Cell(1, 1)) == CellKind.Portal
              && twoPortalFacility.Kind(twoPortalMap, new Cell(0, 1)) == CellKind.NormalWalkable,
            "portal B: the facility names its own cell kind");

        SearchResult jumpSteps = new ShortestStepsPathfinder
        {
            Probe = twoPortalFacility.CreateProbe(twoPortalMap)
        }.Search(twoPortalMap);
        int walkOnlySteps = plainStepWalk.Path.Count - 1;

        Check(jumpSteps.Found && PortalPathIsLegal(twoPortalMap, twoPortalFacility, jumpSteps.Path),
            "portal B: the jumped route is legal");
        Check(PortalFacility.CountTeleports(jumpSteps.Path) >= 1,
            "portal B: the route actually teleports");
        Check(jumpSteps.Path.Count - 1 < walkOnlySteps,
            $"portal B: the jump beats walking ({jumpSteps.Path.Count - 1} < {walkOnlySteps})");
        Check(!jumpSteps.Path.Any(cell => twoPortalFacility.IsPortal(cell)),
            "portal B: the path never stands on a portal");

        // C: four portals, every enabled portal offers every other one.
        GridMap manyPortalMap = BuildOpenMap(7, 7, new Cell(0, 3), new Cell(6, 3));
        var manyPortalFacility = new PortalFacility { TeleportCost = 1 };
        foreach (Cell site in new[] { new Cell(1, 1), new Cell(5, 1), new Cell(1, 5), new Cell(5, 5) })
            manyPortalFacility.Add(manyPortalMap, site, out _);

        Check(manyPortalFacility.Portals.Count == 4, "portal C: four portals coexist");

        var offeredNearFirst = manyPortalFacility.TransitionsFrom(manyPortalMap, new Cell(0, 1));
        var expectedNearFirst = ExpectedPortalExits(manyPortalMap, manyPortalFacility, new Cell(0, 1));

        Check(offeredNearFirst.Select(t => t.Destination).OrderBy(c => c.Y).ThenBy(c => c.X)
                  .SequenceEqual(expectedNearFirst),
            $"portal C: the first portal offers all other portals ({offeredNearFirst.Count} exits)");
        Check(offeredNearFirst.Select(t => t.DestinationPortalId).Distinct().Count() == 3,
            "portal C: three distinct exit portals, never a fixed pairing");

        var offeredNearLast = manyPortalFacility.TransitionsFrom(manyPortalMap, new Cell(6, 5));
        Check(offeredNearLast.Select(t => t.DestinationPortalId).Distinct().Count() == 3,
            "portal C: the last portal also reaches the other three");

        // D: a portal walled in on all four sides is not a usable exit.
        GridMap sealedPortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var sealedPortalFacility = new PortalFacility { TeleportCost = 0 };
        sealedPortalFacility.Add(sealedPortalMap, new Cell(1, 1), out _);
        sealedPortalFacility.Add(sealedPortalMap, new Cell(2, 2), out _);
        foreach (Cell around in sealedPortalMap.Neighbours(new Cell(2, 2)))
            sealedPortalMap.SetCost(around, GridMap.Wall);

        Check(sealedPortalFacility.NearCells(sealedPortalMap, 2).Count == 0,
            "portal D: a walled-in portal has no entry/exit cells");
        Check(!sealedPortalFacility.HasUsableTransitions(sealedPortalMap),
            "portal D: one usable portal alone is not a connection");
        Check(sealedPortalFacility.TransitionsFrom(sealedPortalMap, new Cell(0, 1)).Count == 0,
            "portal D: the sealed portal is never offered as an exit");

        // E: the price decides whether a teleport is worth taking.
        GridMap cheapPortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var cheapPortalFacility = new PortalFacility { TeleportCost = 0 };
        cheapPortalFacility.Add(cheapPortalMap, new Cell(1, 1), out _);
        cheapPortalFacility.Add(cheapPortalMap, new Cell(3, 3), out _);

        SearchResult cheapPortalRoute = new MinCostPathfinder
        {
            Probe = cheapPortalFacility.CreateProbe(cheapPortalMap)
        }.Search(cheapPortalMap);
        SearchResult cheapWalkRoute = new MinCostPathfinder().Search(cheapPortalMap);

        Check(cheapPortalRoute.Found && PortalFacility.CountTeleports(cheapPortalRoute.Path) >= 1,
            "portal E: a free teleport is taken");
        Check(cheapPortalRoute.Cost < cheapWalkRoute.Cost,
            $"portal E: the teleport is cheaper ({cheapPortalRoute.Cost} < {cheapWalkRoute.Cost})");
        Check(PortalPathCost(cheapPortalMap, cheapPortalFacility, cheapPortalRoute.Path) == cheapPortalRoute.Cost,
            "portal E: the reported price survives an independent re-walk");

        GridMap dearPortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var dearPortalFacility = new PortalFacility { TeleportCost = 9 };
        dearPortalFacility.Add(dearPortalMap, new Cell(1, 1), out _);
        dearPortalFacility.Add(dearPortalMap, new Cell(3, 3), out _);

        SearchResult dearPortalRoute = new MinCostPathfinder
        {
            Probe = dearPortalFacility.CreateProbe(dearPortalMap)
        }.Search(dearPortalMap);
        SearchResult dearWalkRoute = new MinCostPathfinder().Search(dearPortalMap);

        Check(dearPortalRoute.Found && PortalFacility.CountTeleports(dearPortalRoute.Path) == 0,
            "portal E: an expensive teleport is refused");
        Check(dearPortalRoute.Cost == dearWalkRoute.Cost,
            $"portal E: the price route matches walking ({dearPortalRoute.Cost} == {dearWalkRoute.Cost})");

        // F: terrain around the candidates decides which exit is taken.
        GridMap exitChoiceMap = BuildOpenMap(5, 5, new Cell(2, 0), new Cell(2, 4));
        var exitChoiceFacility = new PortalFacility { TeleportCost = 2 };
        exitChoiceFacility.Add(exitChoiceMap, new Cell(2, 1), out _);   // entrance
        exitChoiceFacility.Add(exitChoiceMap, new Cell(0, 2), out _);   // dear exit portal
        exitChoiceFacility.Add(exitChoiceMap, new Cell(4, 2), out _);   // cheap exit portal

        foreach (Cell dear in new[] { new Cell(0, 1), new Cell(0, 3), new Cell(1, 2) })
            exitChoiceMap.SetCost(dear, 9);

        SearchResult exitChoiceRoute = new MinCostPathfinder
        {
            Probe = exitChoiceFacility.CreateProbe(exitChoiceMap)
        }.Search(exitChoiceMap);

        Cell landing = FirstJumpLanding(exitChoiceRoute.Path);
        Check(PortalFacility.CountTeleports(exitChoiceRoute.Path) >= 1,
            "portal F: the price route teleports out of the entrance");
        Check(exitChoiceFacility.NearCells(exitChoiceMap, 3).Contains(landing),
            $"portal F: the cheap exit portal wins ({landing.X}, {landing.Y})");
        Check(PortalPathCost(exitChoiceMap, exitChoiceFacility, exitChoiceRoute.Path) == exitChoiceRoute.Cost,
            "portal F: the reported price survives an independent re-walk");

        // G: a portal bridges two regions that ordinary walking cannot connect.
        GridMap splitPortalMap = BuildOpenMap(7, 5, new Cell(0, 2), new Cell(6, 2));
        for (int y = 0; y < 5; y++) splitPortalMap.SetCost(new Cell(3, y), GridMap.Wall);

        var splitPortalFacility = new PortalFacility { TeleportCost = 0 };
        splitPortalFacility.Add(splitPortalMap, new Cell(2, 2), out _);
        splitPortalFacility.Add(splitPortalMap, new Cell(4, 2), out _);

        SearchResult splitWalkRoute = new ShortestStepsPathfinder().Search(splitPortalMap);
        SearchResult splitJumpRoute = new ShortestStepsPathfinder
        {
            Probe = splitPortalFacility.CreateProbe(splitPortalMap)
        }.Search(splitPortalMap);

        Check(!splitWalkRoute.Found, "portal G: the split map is unreachable by walking");
        Check(splitJumpRoute.Found && PortalPathIsLegal(splitPortalMap, splitPortalFacility, splitJumpRoute.Path),
            "portal G: the portal opens a legal route");
        Check(splitJumpRoute.Path.Distinct().Count() == splitJumpRoute.Path.Count,
            "portal G: the bridged route never repeats a cell");

        // H: three portals all reach each other; the search still terminates.
        GridMap cyclePortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var cyclePortalFacility = new PortalFacility { TeleportCost = 0 };
        foreach (Cell site in new[] { new Cell(1, 1), new Cell(3, 1), new Cell(1, 3) })
            cyclePortalFacility.Add(cyclePortalMap, site, out _);

        SearchResult cycleRoute = new ShortestStepsPathfinder
        {
            Probe = cyclePortalFacility.CreateProbe(cyclePortalMap)
        }.Search(cyclePortalMap);

        Check(cycleRoute.Found && PortalPathIsLegal(cyclePortalMap, cyclePortalFacility, cycleRoute.Path),
            "portal H: a cyclic portal graph still ends on a legal route");
        Check(cycleRoute.Path.Distinct().Count() == cycleRoute.Path.Count,
            "portal H: cyclic teleports never repeat a cell");

        // I: closing the exits must be seen by the same facility instance.
        GridMap livePortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var livePortalFacility = new PortalFacility { TeleportCost = 0 };
        livePortalFacility.Add(livePortalMap, new Cell(1, 1), out _);
        livePortalFacility.Add(livePortalMap, new Cell(3, 1), out _);

        SearchResult liveBefore = new ShortestStepsPathfinder
        {
            Probe = livePortalFacility.CreateProbe(livePortalMap)
        }.Search(livePortalMap);
        Check(PortalFacility.CountTeleports(liveBefore.Path) >= 1,
            "portal I: the open exit is used first");

        foreach (Cell around in livePortalMap.Neighbours(new Cell(3, 1)))
            livePortalMap.SetCost(around, GridMap.Wall);

        Check(livePortalFacility.NearCells(livePortalMap, 2).Count == 0,
            "portal I: the cache drops a closed exit set");

        SearchResult liveAfter = new ShortestStepsPathfinder
        {
            Probe = livePortalFacility.CreateProbe(livePortalMap)
        }.Search(livePortalMap);
        Check(liveAfter.Found && PortalFacility.CountTeleports(liveAfter.Path) == 0,
            "portal I: no stale exit is used after the walls close");

        // J: portals that do not pay off are ignored.
        GridMap idlePortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var idlePortalFacility = new PortalFacility { TeleportCost = 9 };
        idlePortalFacility.Add(idlePortalMap, new Cell(4, 0), out _);
        idlePortalFacility.Add(idlePortalMap, new Cell(0, 4), out _);

        SearchResult idleRoute = new MinCostPathfinder
        {
            Probe = idlePortalFacility.CreateProbe(idlePortalMap)
        }.Search(idlePortalMap);
        SearchResult idleWalk = new MinCostPathfinder().Search(idlePortalMap);

        Check(idleRoute.Found && PortalFacility.CountTeleports(idleRoute.Path) == 0,
            "portal J: a portal without profit is ignored");
        Check(idleRoute.Cost == idleWalk.Cost,
            $"portal J: the walking price is kept ({idleRoute.Cost})");

        // Rules: closed cells, no self jumps, fee propagation, removal, gates, switches.
        GridMap rulePortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        rulePortalMap.SetCost(new Cell(2, 2), 4);
        var rulePortalFacility = new PortalFacility { TeleportCost = 3 };
        Check(rulePortalFacility.Add(rulePortalMap, new Cell(2, 2), out _)
              && rulePortalMap.IsWall(new Cell(2, 2)),
            "portal rules: placing a portal closes its cell");
        rulePortalFacility.Add(rulePortalMap, new Cell(0, 2), out _);

        Check(rulePortalFacility.TransitionsFrom(rulePortalMap, new Cell(1, 2))
                  .All(jump => jump.Destination != new Cell(1, 2)),
            "portal rules: a jump never lands on its own entry cell");
        Check(!rulePortalFacility.CreateProbe(rulePortalMap).NormalNeighbours(new Cell(1, 2))
                  .Any(cell => rulePortalFacility.IsPortal(cell)),
            "portal rules: the probe never walks onto a portal cell");
        Check(rulePortalFacility.Portals.All(portal => portal.TeleportCost == 3),
            "portal rules: one fee applies to every portal");

        rulePortalFacility.TeleportCost = 5;
        Check(rulePortalFacility.Portals.All(portal => portal.TeleportCost == 5),
            "portal rules: a fee change reaches every portal");

        Check(rulePortalFacility.RemoveAt(rulePortalMap, new Cell(2, 2))
              && rulePortalMap.CostAt(new Cell(2, 2)) == 4,
            "portal rules: removing a portal restores the old price");
        Check(!rulePortalFacility.Add(rulePortalMap, rulePortalMap.Start, out string? gateNote)
              && gateNote is not null,
            "portal rules: a gate cannot host a portal");

        var switchFacility = new PortalFacility { TeleportCost = 0 };
        GridMap switchPortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        switchFacility.Add(switchPortalMap, new Cell(1, 1), out _);
        switchFacility.Add(switchPortalMap, new Cell(3, 3), out _);

        switchFacility.Enabled = false;
        Check(switchFacility.TransitionsFrom(switchPortalMap, new Cell(1, 0)).Count == 0,
            "portal rules: the master switch stops every jump");
        switchFacility.Enabled = true;
        Check(switchFacility.TransitionsFrom(switchPortalMap, new Cell(1, 0)).Count > 0,
            "portal rules: re-enabling brings the jumps back");

        switchFacility.SetEnabled(2, false);
        Check(switchFacility.TransitionsFrom(switchPortalMap, new Cell(1, 0))
                  .All(jump => jump.DestinationPortalId != 2),
            "portal rules: a disabled portal is never an exit");
        Check(switchFacility.TransitionsFrom(switchPortalMap, new Cell(3, 2)).Count == 0,
            "portal rules: a disabled portal never starts a jump");

        switchFacility.Reset();
        Check(switchFacility.Portals.Count == 0 && !switchFacility.HasUsableTransitions(switchPortalMap),
            "portal rules: reset forgets every portal");

        // A single portal has no partner, so it can never be used, and it must not break the map.
        var singleFacility = new PortalFacility { TeleportCost = 0 };
        GridMap singlePortalMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        singleFacility.Add(singlePortalMap, new Cell(2, 2), out _);

        Check(singleFacility.TransitionsFrom(singlePortalMap, new Cell(1, 2)).Count == 0,
            "portal rules: a lone portal offers no connection");
        Check(!singleFacility.HasUsableTransitions(singlePortalMap),
            "portal rules: a lone portal is not a usable facility");
        Check(new ShortestStepsPathfinder { Probe = singleFacility.CreateProbe(singlePortalMap) }
                  .Search(singlePortalMap).Found,
            "portal rules: one closed cell does not disconnect an open map");

        // A probe can be rebound to a cloned map, which is how 03 and 05 search their legs.
        var rebindFacility = new PortalFacility { TeleportCost = 1 };
        GridMap rebindMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        rebindFacility.Add(rebindMap, new Cell(1, 1), out _);
        rebindFacility.Add(rebindMap, new Cell(3, 3), out _);
        GridMap rebindClone = rebindMap.Clone();
        var rebindOriginal = rebindFacility.CreateProbe(rebindMap)
            .SpecialTransitions(new Cell(1, 0)).ToList();
        var rebindCopy = rebindFacility.CreateProbe(rebindMap).For(rebindClone)
            .SpecialTransitions(new Cell(1, 0)).ToList();

        Check(rebindOriginal.Count > 0
              && rebindCopy.Select(jump => jump.Destination).SequenceEqual(
                  rebindOriginal.Select(jump => jump.Destination)),
            "portal probe: a clone sees the same connections");

        // 02: a pocket reachable only by portal must join the farthest comparison.
        GridMap pocketMap = BuildOpenMap(7, 5, new Cell(0, 1), new Cell(6, 0));
        pocketMap.SetCost(new Cell(5, 4), GridMap.Wall);
        pocketMap.SetCost(new Cell(6, 3), GridMap.Wall);

        var pocketFacility = new PortalFacility { TeleportCost = 0 };
        pocketFacility.Add(pocketMap, new Cell(5, 4), out _);
        pocketFacility.Add(pocketMap, new Cell(6, 3), out _);

        FarthestPointResult pocketPlain =
            new FarthestFromTargetFinder().Find(pocketMap, new Cell(0, 1));
        FarthestPointResult pocketProbe = new FarthestFromTargetFinder
        {
            Probe = pocketFacility.CreateProbe(pocketMap)
        }.Find(pocketMap, new Cell(0, 1));

        Check(pocketPlain.Found && pocketPlain.Distances[6, 4] == FarthestPointResult.Unreachable,
            "portal 02: the sealed pocket is unreachable without a probe");
        Check(pocketProbe.Found && pocketProbe.Cell == new Cell(6, 4),
            $"portal 02: the portal pocket becomes the farthest cell ({pocketProbe.Cell})");
        Check(pocketProbe.Distance == pocketPlain.Distance + 1,
            $"portal 02: the jump adds one move ({pocketProbe.Distance} > {pocketPlain.Distance})");

        // 03: both legs have to jump when the farthest cell sits in a sealed pocket.
        GridMap loiterPocketMap = BuildOpenMap(7, 5, new Cell(0, 1), new Cell(6, 0));
        loiterPocketMap.SetCost(new Cell(5, 3), GridMap.Wall);
        loiterPocketMap.SetCost(new Cell(5, 4), GridMap.Wall);
        loiterPocketMap.SetCost(new Cell(6, 2), GridMap.Wall);

        var loiterFacility = new PortalFacility { TeleportCost = 0 };
        loiterFacility.Add(loiterPocketMap, new Cell(5, 4), out _);   // outside gate
        loiterFacility.Add(loiterPocketMap, new Cell(5, 3), out _);   // pocket gate

        var loiterProbe = new FarthestLoiterPathfinder
        {
            ReferencePoint = new Cell(0, 1),
            Probe = loiterFacility.CreateProbe(loiterPocketMap)
        };
        SearchResult loiterProbeRoute = loiterProbe.Search(loiterPocketMap);
        FarthestPointResult loiterTurn = new FarthestFromTargetFinder
        {
            Probe = loiterFacility.CreateProbe(loiterPocketMap)
        }.Find(loiterPocketMap, new Cell(0, 1));

        Check(loiterProbeRoute.Found && loiterProbe.FarthestPoint == loiterTurn.Cell,
            $"portal 03: the route turns at 02's farthest cell ({loiterProbe.FarthestPoint})");
        Check(loiterProbeRoute.Path.Contains(loiterTurn.Cell),
            "portal 03: the farthest cell lies on the route");
        int loiterJumps = PortalFacility.CountTeleports(loiterProbeRoute.Path);
        Check(PortalPathIsLegal(loiterPocketMap, loiterFacility, loiterProbeRoute.Path,
                  requireSimple: false)
              && loiterJumps >= 1,
            $"portal 03: the legs jump in and out of the pocket ({loiterJumps})");
        Check(PortalPathCost(loiterPocketMap, loiterFacility, loiterProbeRoute.Path)
              == loiterProbeRoute.Cost,
            $"portal 03: the reported price survives an independent re-walk ({loiterProbeRoute.Cost})");

        // 05: the cluster sits across a walled split, so only a portal can reach its ring.
        GridMap splitClusterMap = BuildOpenMap(9, 5, new Cell(0, 2), new Cell(8, 4));
        for (int y = 0; y < 5; y++) splitClusterMap.SetCost(new Cell(4, y), GridMap.Wall);
        splitClusterMap.SetCost(new Cell(6, 1), GridMap.Wall);

        var splitClusterFacility = new PortalFacility { TeleportCost = 0 };
        splitClusterFacility.Add(splitClusterMap, new Cell(2, 2), out _);
        splitClusterFacility.Add(splitClusterMap, new Cell(6, 2), out _);

        var splitClusterPlain = new ObstacleClusterPathfinder { ReferencePoint = new Cell(6, 1) };
        SearchResult splitClusterWalk = splitClusterPlain.Search(splitClusterMap);

        var splitClusterProbe = new ObstacleClusterPathfinder
        {
            ReferencePoint = new Cell(6, 1),
            Probe = splitClusterFacility.CreateProbe(splitClusterMap)
        };
        SearchResult splitClusterRoute = splitClusterProbe.Search(splitClusterMap);

        Check(!splitClusterWalk.Found,
            "portal 05: the ring is unreachable while the map stays split");
        Check(splitClusterRoute.Found
              && splitClusterProbe.ViaCell is { } clusterVia
              && splitClusterProbe.RingCells.Contains(clusterVia),
            "portal 05: the portal lets the route touch the far ring cell");
        Check(PortalPathIsLegal(splitClusterMap, splitClusterFacility, splitClusterRoute.Path)
              && PortalFacility.CountTeleports(splitClusterRoute.Path) >= 1,
            "portal 05: the constrained route jumps across the split");
        Check(PortalPathCost(splitClusterMap, splitClusterFacility, splitClusterRoute.Path)
              == splitClusterRoute.Cost,
            $"portal 05: the reported price survives an independent re-walk ({splitClusterRoute.Cost})");

        // 06: a start sealed behind portals can only leave by jumping.
        GridMap sealedStartMap = BuildOpenMap(5, 3, new Cell(0, 0), new Cell(4, 0));
        sealedStartMap.SetCost(new Cell(1, 0), GridMap.Wall);
        sealedStartMap.SetCost(new Cell(0, 1), GridMap.Wall);

        var sealedStartFacility = new PortalFacility { TeleportCost = 0 };
        sealedStartFacility.Add(sealedStartMap, new Cell(1, 0), out _);
        sealedStartFacility.Add(sealedStartMap, new Cell(0, 1), out _);

        var sealedScorePlain = new MaxPerpendicularPathfinder
        {
            ReferencePoint = new Cell(4, 2),
            MaxExpandedNodes = 3_000_000,
            MaxMilliseconds = 5_000
        };
        SearchResult sealedScoreWalk = sealedScorePlain.Search(sealedStartMap);

        var sealedScoreProbe = new MaxPerpendicularPathfinder
        {
            ReferencePoint = new Cell(4, 2),
            Probe = sealedStartFacility.CreateProbe(sealedStartMap),
            MaxExpandedNodes = 3_000_000,
            MaxMilliseconds = 5_000
        };
        SearchResult sealedScoreRoute = sealedScoreProbe.Search(sealedStartMap);

        Check(!sealedScoreWalk.Found, "portal 06: a sealed start has no plain route");
        Check(sealedScoreRoute.Found
              && PortalPathIsLegal(sealedStartMap, sealedStartFacility, sealedScoreRoute.Path)
              && PortalFacility.CountTeleports(sealedScoreRoute.Path) >= 1,
            "portal 06: the route jumps out of the sealed start");
        Check(Math.Abs(sealedScoreRoute.Score
                       - MaxPerpendicularPathfinder.CalculatePathScore(
                           sealedScoreRoute.Path, sealedScoreProbe.ReferencePoint!.Value)) < 1e-9,
            $"portal 06: score {sealedScoreRoute.Score} survives an independent re-score");
        Check(sealedScoreRoute.Path.Distinct().Count() == sealedScoreRoute.Path.Count,
            "portal 06: the jumped route never repeats a cell");

        // 07: a jump that lands on a reference counts as one crossing.
        GridMap portalReferenceMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var portalReferenceFacility = new PortalFacility { TeleportCost = 0 };
        portalReferenceFacility.Add(portalReferenceMap, new Cell(1, 1), out _);
        portalReferenceFacility.Add(portalReferenceMap, new Cell(3, 3), out _);

        Cell[] doorwayReferences = { new(4, 3) };   // one exit cell is a reference
        var portalReferenceModule = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = doorwayReferences,
            Probe = portalReferenceFacility.CreateProbe(portalReferenceMap)
        };
        SearchResult portalReferenceRoute = portalReferenceModule.Search(portalReferenceMap);

        Check(portalReferenceRoute.Found
              && PortalFacility.CountTeleports(portalReferenceRoute.Path) >= 1,
            "portal 07: the route jumps instead of walking around");
        Check(portalReferenceRoute.ReferenceCount == 0
              && !portalReferenceRoute.Path.Contains(new Cell(4, 3)),
            "portal 07: the pricey reference exit is skipped");
        Check(MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(
                  portalReferenceRoute, portalReferenceMap, doorwayReferences,
                  portalReferenceModule.Probe) is null,
            "portal 07: validation knows a jump when it sees one");

        // 07: when every landing is a reference, exactly one crossing is unavoidable.
        GridMap onlyPortalMap = BuildOpenMap(5, 3, new Cell(0, 0), new Cell(4, 0));
        onlyPortalMap.SetCost(new Cell(1, 0), GridMap.Wall);
        onlyPortalMap.SetCost(new Cell(0, 1), GridMap.Wall);
        var onlyPortalFacility = new PortalFacility { TeleportCost = 0 };
        onlyPortalFacility.Add(onlyPortalMap, new Cell(1, 0), out _);
        onlyPortalFacility.Add(onlyPortalMap, new Cell(0, 1), out _);

        Cell[] allLandingReferences = { new(2, 0), new(1, 1), new(0, 2) };
        SearchResult onlyPortalRoute = new MinimumReferenceTraversalPathfinder
        {
            ReferenceCells = allLandingReferences,
            Probe = onlyPortalFacility.CreateProbe(onlyPortalMap)
        }.Search(onlyPortalMap);

        Check(onlyPortalRoute.Found && onlyPortalRoute.ReferenceCount >= 1,
            $"portal 07: landing on a reference is counted ({onlyPortalRoute.ReferenceCount})");
        Check(MinimumReferenceTraversalPathfinder.ValidatePathfindingResult(
                  onlyPortalRoute, onlyPortalMap, allLandingReferences,
                  onlyPortalFacility.CreateProbe(onlyPortalMap)) is null,
            "portal 07: the forced crossing survives validation");

        // ---- one-way passage: a still-walkable cell that forbids exactly two edges ----

        // Test 1: a right arrow forbids head-entry and tail-exit, sides stay open.
        GridMap oneWayMap = BuildOpenMap(5, 5, new Cell(0, 2), new Cell(4, 2));
        var oneWayFacility = new OneWayFacility();
        IPathProbe oneWayProbe = oneWayFacility.ApplyTo(OneWayFacility.Plain(oneWayMap));

        Check(oneWayFacility.Add(oneWayMap, new Cell(2, 2), Direction.Right, out _),
            "oneway 1: placing a right arrow succeeds");
        var passageCentre = new Cell(2, 2);

        Check(oneWayProbe.NormalNeighbours(new Cell(1, 2)).Contains(passageCentre),
            "oneway 1: entering from the tail side is allowed");
        Check(oneWayProbe.NormalNeighbours(passageCentre).Contains(new Cell(3, 2)),
            "oneway 1: leaving toward the head side is allowed");
        Check(!oneWayProbe.NormalNeighbours(new Cell(3, 2)).Contains(passageCentre),
            "oneway 1: entering from the head side is forbidden");
        Check(!oneWayProbe.NormalNeighbours(passageCentre).Contains(new Cell(1, 2)),
            "oneway 1: leaving toward the tail side is forbidden");
        Check(oneWayProbe.NormalNeighbours(passageCentre).Contains(new Cell(2, 1))
              && oneWayProbe.NormalNeighbours(passageCentre).Contains(new Cell(2, 3))
              && oneWayProbe.NormalNeighbours(new Cell(2, 1)).Contains(passageCentre)
              && oneWayProbe.NormalNeighbours(new Cell(2, 3)).Contains(passageCentre),
            "oneway 1: both sides stay bidirectional in the edge pool");

        // Crossing the passage is a straight line: the entry decides the only exit.
        Check(oneWayProbe.ExitsAfter(new Cell(1, 2), passageCentre).SequenceEqual(new[] { new Cell(3, 2) }),
            "oneway 1: a tail entry leaves straight through the head");
        Check(oneWayProbe.ExitsAfter(new Cell(2, 1), passageCentre).SequenceEqual(new[] { new Cell(2, 3) })
              && oneWayProbe.ExitsAfter(new Cell(2, 3), passageCentre).SequenceEqual(new[] { new Cell(2, 1) }),
            "oneway 1: a side entry leaves straight through the opposite side");
        Check(!oneWayProbe.ExitsAfter(new Cell(2, 1), passageCentre).Contains(new Cell(2, 1)),
            "oneway 1: a side entry may not bounce back or turn");

        // An arrival without geometry (teleport landing, search origin) keeps every
        // exit except the tail side, so the passage keeps its one-way identity.
        var landingExits = oneWayProbe.ExitsAfter(passageCentre, passageCentre).ToList();
        Check(landingExits.Contains(new Cell(3, 2)) && landingExits.Contains(new Cell(2, 1))
              && landingExits.Contains(new Cell(2, 3)) && !landingExits.Contains(new Cell(1, 2)),
            "oneway 1: an arrival without geometry keeps every exit except the tail side");

        // Test 2: the same four rules rotated for the other three arrows.
        foreach ((Direction arrow, Cell step) in new[]
                 {
                     (Direction.Up, new Cell(0, -1)),
                     (Direction.Down, new Cell(0, 1)),
                     (Direction.Left, new Cell(-1, 0))
                 })
        {
            GridMap rotateMap = BuildOpenMap(5, 5, new Cell(0, 2), new Cell(4, 2));
            var rotateFacility = new OneWayFacility();
            IPathProbe rotateProbe = rotateFacility.ApplyTo(OneWayFacility.Plain(rotateMap));
            rotateFacility.Add(rotateMap, passageCentre, arrow, out _);

            Cell head = new(passageCentre.X + step.X, passageCentre.Y + step.Y);
            Cell tail = new(passageCentre.X - step.X, passageCentre.Y - step.Y);

            Check(rotateProbe.NormalNeighbours(tail).Contains(passageCentre),
                $"oneway 2: {arrow} allows entering from the tail");
            Check(rotateProbe.NormalNeighbours(passageCentre).Contains(head),
                $"oneway 2: {arrow} allows leaving toward the head");
            Check(!rotateProbe.NormalNeighbours(head).Contains(passageCentre),
                $"oneway 2: {arrow} forbids entering from the head");
            Check(!rotateProbe.NormalNeighbours(passageCentre).Contains(tail),
                $"oneway 2: {arrow} forbids leaving toward the tail");

            // Rotated straight-line rule: the tail entry exits at the head, a
            // perpendicular side entry exits at the opposite side.
            Check(rotateProbe.ExitsAfter(tail, passageCentre).SequenceEqual(new[] { head }),
                $"oneway 2: {arrow} crossing leaves straight through");
            Cell side = new(passageCentre.X - step.Y, passageCentre.Y - step.X);
            Cell opposite = new(passageCentre.X + step.Y, passageCentre.Y + step.X);
            Check(rotateProbe.ExitsAfter(side, passageCentre).SequenceEqual(new[] { opposite }),
                $"oneway 2: {arrow} side crossing also goes straight");
        }

        // Test 3: passages on every border; missing head/tail removes an edge, not the passage.
        GridMap edgeMap = BuildOpenMap(5, 5, new Cell(0, 2), new Cell(4, 2));
        var edgeFacility = new OneWayFacility();
        IPathProbe edgeProbe = edgeFacility.ApplyTo(OneWayFacility.Plain(edgeMap));

        bool allPlaced = edgeFacility.Add(edgeMap, new Cell(0, 0), Direction.Left, out _)   // head off-map
                         && edgeFacility.Add(edgeMap, new Cell(4, 0), Direction.Right, out _) // tail off-map
                         && edgeFacility.Add(edgeMap, new Cell(0, 4), Direction.Up, out _)
                         && edgeFacility.Add(edgeMap, new Cell(4, 4), Direction.Down, out _);
        Check(allPlaced && edgeFacility.Passages.Count == 4,
            "oneway 3: passages exist on every border");

        bool inBoundsOnly = true;
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 5; x++)
            foreach (Cell next in edgeProbe.NormalNeighbours(new Cell(x, y)))
                if (!edgeMap.InBounds(next))
                    inBoundsOnly = false;
        Check(inBoundsOnly, "oneway 3: the probe never offers an out-of-bounds cell");

        var cornerPassage = new Cell(0, 0);   // Left arrow: tail (1,0), head off-map
        Check(edgeProbe.NormalNeighbours(new Cell(1, 0)).Contains(cornerPassage)
              && !edgeProbe.NormalNeighbours(cornerPassage).Contains(new Cell(1, 0))
              && edgeProbe.NormalNeighbours(cornerPassage).Contains(new Cell(0, 1))
              && edgeProbe.NormalNeighbours(new Cell(0, 1)).Contains(cornerPassage),
            "oneway 3: a border passage constrains only the edges that exist");

        // Test 4: walls around a passage keep their own rules.
        GridMap obstacleMap = BuildOpenMap(5, 5, new Cell(0, 2), new Cell(4, 2));
        obstacleMap.SetCost(new Cell(3, 2), GridMap.Wall);   // head side is a wall
        obstacleMap.SetCost(new Cell(2, 1), GridMap.Wall);   // one side is a wall
        var obstacleFacility = new OneWayFacility();
        IPathProbe obstacleProbe = obstacleFacility.ApplyTo(OneWayFacility.Plain(obstacleMap));
        obstacleFacility.Add(obstacleMap, new Cell(2, 2), Direction.Right, out _);

        Check(!obstacleProbe.NormalNeighbours(new Cell(2, 2)).Contains(new Cell(3, 2)),
            "oneway 4: a wall on the head side still blocks the move");
        Check(obstacleProbe.NormalNeighbours(new Cell(2, 2)).Contains(new Cell(2, 3))
              && !obstacleProbe.NormalNeighbours(new Cell(2, 2)).Contains(new Cell(2, 1)),
            "oneway 4: the open side stays open, the walled side stays shut");

        // Test 5: BFS and Dijkstra both honour arrows as map-level rules.
        GridMap arrowDetourMap = BuildOpenMap(5, 3, new Cell(0, 1), new Cell(4, 1));
        var detourFacility = new OneWayFacility();
        IPathProbe detourProbe = detourFacility.ApplyTo(OneWayFacility.Plain(arrowDetourMap));
        detourFacility.Add(arrowDetourMap, new Cell(1, 1), Direction.Left, out _);
        detourFacility.Add(arrowDetourMap, new Cell(2, 1), Direction.Left, out _);
        detourFacility.Add(arrowDetourMap, new Cell(3, 1), Direction.Left, out _);

        SearchResult plainCorridor = new ShortestStepsPathfinder().Search(arrowDetourMap);
        Check(plainCorridor.Found && plainCorridor.Path.Count == 5,
            "oneway 5: the same corridor is a straight 4-move walk without arrows");

        SearchResult detourSteps = new ShortestStepsPathfinder { Probe = detourProbe }.Search(arrowDetourMap);
        SearchResult detourCost = new MinCostPathfinder { Probe = detourProbe }.Search(arrowDetourMap);

        Check(detourSteps.Found && detourSteps.Path.Count == 7,
            $"oneway 5: BFS detours around the reversed corridor ({detourSteps.Path.Count - 1} moves)");
        Check(detourCost.Found && detourCost.Path.Count == 7,
            $"oneway 5: Dijkstra detours around the reversed corridor ({detourCost.Path.Count - 1} moves)");
        Check(OneWayPathIsLegal(arrowDetourMap, detourFacility, detourSteps.Path)
              && OneWayPathIsLegal(arrowDetourMap, detourFacility, detourCost.Path),
            "oneway 5: both detours respect every arrow");

        GridMap sealedCorridorMap = BuildOpenMap(5, 2, new Cell(0, 0), new Cell(4, 0));
        for (int x = 0; x < 5; x++) sealedCorridorMap.SetCost(new Cell(x, 1), GridMap.Wall);
        var sealedCorridorFacility = new OneWayFacility();
        IPathProbe sealedCorridorProbe =
            sealedCorridorFacility.ApplyTo(OneWayFacility.Plain(sealedCorridorMap));
        sealedCorridorFacility.Add(sealedCorridorMap, new Cell(1, 0), Direction.Left, out _);
        sealedCorridorFacility.Add(sealedCorridorMap, new Cell(2, 0), Direction.Left, out _);
        sealedCorridorFacility.Add(sealedCorridorMap, new Cell(3, 0), Direction.Left, out _);

        SearchResult sealedCorridorSteps =
            new ShortestStepsPathfinder { Probe = sealedCorridorProbe }.Search(sealedCorridorMap);
        Check(!sealedCorridorSteps.Found,
            "oneway 5: arrows against the only corridor leave no route at all");

        // Test 6: every module's route, one sweep, checked pair by pair.
        GridMap sweepMap = BuildOpenMap(7, 7, new Cell(0, 3), new Cell(6, 3));
        var sweepFacility = new OneWayFacility();
        IPathProbe sweepProbe = sweepFacility.ApplyTo(OneWayFacility.Plain(sweepMap));
        sweepFacility.Add(sweepMap, new Cell(2, 3), Direction.Up, out _);
        sweepFacility.Add(sweepMap, new Cell(4, 3), Direction.Down, out _);
        sweepFacility.Add(sweepMap, new Cell(3, 1), Direction.Left, out _);

        bool sweepLegal = true;
        foreach (IPathfinder template in algorithms)
        {
            IPathfinder module = AlgorithmHost.Create(template);
            if (module is IReferencePointModule sweepReference)
                sweepReference.ReferencePoint = ObstacleCluster.FirstWall(sweepMap) ?? sweepMap.Goal;
            if (module is ITransitionAware sweepAware)
                sweepAware.Probe = sweepProbe;

            SearchResult sweepResult = module.Search(sweepMap);
            if (sweepResult.Found && !OneWayPathIsLegal(sweepMap, sweepFacility, sweepResult.Path))
            {
                sweepLegal = false;
                Console.WriteLine($"  {template.Id} produced a route that crosses an arrow");
            }
        }
        Check(sweepLegal, "oneway 6: every discovered module keeps every arrow");

        // Arrows + portals share one probe: jumps pass, the walk away from a landing binds.
        GridMap mixMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var mixPortals = new PortalFacility { TeleportCost = 0 };
        mixPortals.Add(mixMap, new Cell(1, 1), out _);
        mixPortals.Add(mixMap, new Cell(3, 3), out _);
        var mixOneWay = new OneWayFacility();
        mixOneWay.Add(mixMap, new Cell(2, 2), Direction.Right, out _);
        IPathProbe mixProbe = mixOneWay.ApplyTo(mixPortals.CreateProbe(mixMap));

        var besideGate = new Cell(0, 1);
        Check(mixProbe.SpecialTransitions(besideGate).Count()
              == mixPortals.TransitionsFrom(mixMap, besideGate).Count(),
            "oneway + portal: teleports pass through the arrow filter untouched");
        Check(!mixProbe.NormalNeighbours(new Cell(2, 2)).Contains(new Cell(1, 2)),
            "oneway + portal: the arrow still binds the walk away from a landing");

        Check(!detourProbe.For(arrowDetourMap.Clone())
                  .NormalNeighbours(new Cell(0, 1)).Contains(new Cell(1, 1)),
            "oneway probe: a rebinding clone keeps the same arrows");

        // Test 7: a single-cell corridor where the only route must cross straight.
        GridMap straightMap = BuildOpenMap(3, 5, new Cell(1, 4), new Cell(1, 0));
        var straightFacility = new OneWayFacility();
        IPathProbe straightProbe = straightFacility.ApplyTo(OneWayFacility.Plain(straightMap));
        straightFacility.Add(straightMap, new Cell(1, 2), Direction.Up, out _);

        SearchResult straightSteps =
            new ShortestStepsPathfinder { Probe = straightProbe }.Search(straightMap);
        Check(straightSteps.Found
              && straightSteps.Path.SequenceEqual(new[]
              {
                  new Cell(1, 4), new Cell(1, 3), new Cell(1, 2), new Cell(1, 1), new Cell(1, 0)
              }),
            "oneway 7: the only corridor route crosses the arrow in a straight line");
        Check(OneWayPathIsLegal(straightMap, straightFacility, straightSteps.Path),
            "oneway 7: the straight crossing passes the route audit");
        Check(!straightProbe.ExitsAfter(new Cell(1, 3), new Cell(1, 2))
                  .Contains(new Cell(0, 2))
              && !straightProbe.ExitsAfter(new Cell(1, 3), new Cell(1, 2))
                  .Contains(new Cell(2, 2)),
            "oneway 7: no side exit exists once the passage is entered by foot");

        SearchResult straightCost =
            new MinCostPathfinder { Probe = straightProbe }.Search(straightMap);
        Check(straightCost.Found && straightCost.Path.Count == 5,
            "oneway 7: Dijkstra also crosses straight when no detour exists");

        // Test 8: a teleport may land on a passage cell; the landing keeps every
        // exit except the tail side, so the passage identity survives the jump.
        GridMap landMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var landPortals = new PortalFacility { TeleportCost = 0 };
        landPortals.Add(landMap, new Cell(1, 1), out _);
        landPortals.Add(landMap, new Cell(3, 1), out _);
        var landOneWay = new OneWayFacility();
        landOneWay.Add(landMap, new Cell(3, 2), Direction.Right, out _);
        IPathProbe landProbe = landOneWay.ApplyTo(landPortals.CreateProbe(landMap));

        Check(landPortals.NearCells(landMap, 2).Contains(new Cell(3, 2)),
            "oneway landing: the passage cell really is a teleport landing");
        var jumpLanding = landProbe.ExitsAfter(new Cell(3, 2), new Cell(3, 2)).ToList();
        Check(jumpLanding.Contains(new Cell(4, 2)) && jumpLanding.Contains(new Cell(3, 3))
              && !jumpLanding.Contains(new Cell(2, 2)),
            "oneway landing: a jump landing keeps every exit except the tail side");

        // ---- directional launcher: a still-walkable pad with one optional straight transfer ----

        // Test 1: four directions, a positive distance, and a distance the user may edit.
        GridMap launchDirMap = BuildOpenMap(9, 9, new Cell(0, 8), new Cell(8, 0));
        var dirFacility = new DirectionalLauncherFacility();
        Cell padCentre = new(4, 4);
        Check(dirFacility.Add(launchDirMap, padCentre, Direction.Up, 3, out _),
            "launcher 1: placing an up pad succeeds");
        DirectionalLauncher padUp = dirFacility.At(padCentre)!;

        foreach ((Direction direction, Cell target) in new[]
                 {
                     (Direction.Up, new Cell(4, 1)),
                     (Direction.Down, new Cell(4, 7)),
                     (Direction.Left, new Cell(1, 4)),
                     (Direction.Right, new Cell(7, 4))
                 })
        {
            Check(dirFacility.SetDirection(padUp.Id, direction) && padUp.Target == target,
                $"launcher 1: {direction} target is ({target.X},{target.Y})");

            LauncherChainResult head = dirFacility.ResolveChain(launchDirMap, padUp);
            Check(head.Success && head.FinalCell == target
                  && head.LandingPositions.SequenceEqual(new[] { target })
                  && head.GeometricDistance == 3,
                $"launcher 1: {direction} chain lands exactly on the target");
        }

        Check(dirFacility.SetDistance(padUp.Id, 4) && padUp.TransferDistance == 4
              && dirFacility.ResolveChain(launchDirMap, padUp).FinalCell == new Cell(8, 4),
            "launcher 1: an edited distance moves the target to (8,4)");
        Check(!dirFacility.SetDistance(padUp.Id, 0) && padUp.TransferDistance == 4,
            "launcher 1: a non-positive distance is refused");

        // Test 2: the pad cell stays ordinary terrain; walking across it never fires it.
        GridMap crossMap = BuildOpenMap(5, 5, new Cell(0, 2), new Cell(4, 2));
        var crossFacility = new DirectionalLauncherFacility();
        IPathProbe crossProbe = crossFacility.CreateProbe(crossMap);
        crossFacility.Add(crossMap, new Cell(2, 2), Direction.Down, 2, out _);
        Cell crossPad = new(2, 2);

        Check(crossProbe.NormalNeighbours(crossPad).Count() == 4,
            "launcher 2: the pad cell keeps all four ordinary exits");
        Check(new[] { new Cell(1, 2), new Cell(3, 2), new Cell(2, 1), new Cell(2, 3) }
                .All(side => crossProbe.NormalNeighbours(side).Contains(crossPad)),
            "launcher 2: every legal direction may walk into the pad");

        SearchResult crossing = new ShortestStepsPathfinder { Probe = crossProbe }.Search(crossMap);
        Check(crossing.Found
              && crossing.Path.SequenceEqual(new[]
              {
                  new Cell(0, 2), new Cell(1, 2), new Cell(2, 2), new Cell(3, 2), new Cell(4, 2)
              }),
            "launcher 2: the straight walk crosses the pad without firing it");
        Check(LauncherPathPlan.Attach(crossMap, crossProbe, crossing).Jumps.Count == 0,
            "launcher 2: no launcher event appears on a plain crossing");

        // Test 3: active use is one optional edge from any adjacent cell.
        GridMap useMap = BuildOpenMap(9, 3, new Cell(0, 1), new Cell(6, 1));
        var useFacility = new DirectionalLauncherFacility();
        IPathProbe useProbe = useFacility.CreateProbe(useMap);
        useFacility.Add(useMap, new Cell(2, 1), Direction.Right, 3, out _);
        DirectionalLauncher usePad = useFacility.At(new Cell(2, 1))!;

        var useJumps = useProbe.SpecialTransitions(new Cell(1, 1)).ToList();
        Check(useJumps.Count == 1 && useJumps[0].Destination == new Cell(5, 1)
              && useJumps[0].Type == TransitionType.Launcher,
            "launcher 3: standing next to a pad offers its jump without stepping on it");
        Check(useJumps[0].TriggeredLauncherIds is { } useIds
              && useIds.SequenceEqual(new[] { usePad.Id })
              && useJumps[0].LandingPositions is { } useLandings
              && useLandings.SequenceEqual(new[] { new Cell(5, 1) }),
            "launcher 3: the jump carries its pad id and landing");
        Check(useProbe.SpecialTransitions(new Cell(2, 1)).Any(jump => jump.Destination == new Cell(5, 1)),
            "launcher 3: the pad itself may also be fired after stepping onto it");

        SearchResult useSteps = new ShortestStepsPathfinder { Probe = useProbe }.Search(useMap);
        Check(useSteps.Found && useSteps.Path.Count == 4 && useSteps.Path[2] == new Cell(5, 1),
            "launcher 3: BFS takes one jump edge and lands on (5,1)");
        Check(LauncherPathIsLegal(useMap, useFacility, useSteps.Path),
            "launcher 3: the jumped route passes the independent audit");

        LauncherPathPlan usePlan = LauncherPathPlan.Attach(useMap, useProbe, useSteps);
        Check(usePlan.Resolved && useSteps.LauncherJumps.Count == 1
              && useSteps.LauncherJumps[0].From == new Cell(1, 1)
              && useSteps.LauncherJumps[0].Destination == new Cell(5, 1),
            "launcher 3: the result records the launcher move, not just coordinates");

        // A changed landing takes effect on the very next query: no cache to invalidate.
        useMap.SetCost(new Cell(5, 1), GridMap.Wall);
        Check(useProbe.SpecialTransitions(new Cell(1, 1)).Count() == 0,
            "launcher 3: walling the landing removes the jump immediately");
        useMap.SetCost(new Cell(5, 1), GridMap.MinCost);
        Check(useProbe.SpecialTransitions(new Cell(1, 1)).Count() == 1,
            "launcher 3: reopening the landing brings the jump back");

        // Test 4: cells crossed by the transfer are never checked.
        GridMap midMap = BuildOpenMap(9, 3, new Cell(0, 1), new Cell(5, 1));
        var midFacility = new DirectionalLauncherFacility();
        IPathProbe midProbe = midFacility.CreateProbe(midMap);
        midFacility.Add(midMap, new Cell(1, 1), Direction.Right, 4, out _);
        midMap.SetCost(new Cell(2, 1), GridMap.Wall);
        midMap.SetCost(new Cell(3, 1), GridMap.Wall);
        midMap.SetCost(new Cell(4, 1), GridMap.Wall);

        LauncherChainResult midChain = midFacility.ResolveChain(midMap, new Cell(1, 1));
        Check(midChain.Success && midChain.FinalCell == new Cell(5, 1),
            "launcher 4: obstacles between the pad and its landing do not stop the transfer");
        Check(midProbe.SpecialTransitions(new Cell(0, 1)).Any(jump => jump.Destination == new Cell(5, 1)),
            "launcher 4: the jump is offered from the cell next to the pad");

        SearchResult midSteps = new ShortestStepsPathfinder { Probe = midProbe }.Search(midMap);
        Check(midSteps.Found && midSteps.Path.Count == 2 && midSteps.Path[1] == new Cell(5, 1),
            "launcher 4: one jump crosses the walled middle in one move");
        Check(LauncherPathIsLegal(midMap, midFacility, midSteps.Path),
            "launcher 4: the audited route only checks landings");

        // Test 5: an impassable final landing kills the move; walking stays legal.
        GridMap landingBlockMap = BuildOpenMap(9, 3, new Cell(0, 1), new Cell(8, 2));
        var landingBlockFacility = new DirectionalLauncherFacility();
        IPathProbe landingBlockProbe = landingBlockFacility.CreateProbe(landingBlockMap);
        landingBlockFacility.Add(landingBlockMap, new Cell(1, 1), Direction.Right, 4, out _);
        landingBlockMap.SetCost(new Cell(5, 1), GridMap.Wall);

        LauncherChainResult landingBlock = landingBlockFacility.ResolveChain(landingBlockMap, new Cell(1, 1));
        Check(!landingBlock.Success && landingBlock.Error == LauncherChainError.Blocked
              && landingBlock.FinalCell is null,
            "launcher 5: a walled final landing makes the whole move unavailable");
        Check(landingBlockProbe.SpecialTransitions(new Cell(0, 1)).Count() == 0,
            "launcher 5: no jump is published for a blocked landing");
        Check(landingBlockProbe.NormalNeighbours(landingBlockMap.Neighbours(new Cell(1, 1))
                  .First(cell => !landingBlockMap.IsWall(cell))).Contains(new Cell(1, 1)),
            "launcher 5: ordinary walking across the pad is still allowed");

        SearchResult landingBlockSteps =
            new ShortestStepsPathfinder { Probe = landingBlockProbe }.Search(landingBlockMap);
        Check(landingBlockSteps.Found
              && LauncherPathIsLegal(landingBlockMap, landingBlockFacility, landingBlockSteps.Path)
              && LauncherPathPlan.Attach(landingBlockMap, landingBlockProbe, landingBlockSteps).Jumps.Count == 0,
            "launcher 5: the route walks normally and fires no pad");

        // Test 6: a landing on a live pad forces the next pad, and so on.
        GridMap chainMap = BuildOpenMap(9, 5, new Cell(0, 0), new Cell(5, 3));
        var chainFacility = new DirectionalLauncherFacility();
        IPathProbe chainProbe = chainFacility.CreateProbe(chainMap);
        chainFacility.Add(chainMap, new Cell(1, 1), Direction.Right, 2, out _);  // #1 -> (3,1)
        chainFacility.Add(chainMap, new Cell(3, 1), Direction.Down, 2, out _);   // #2 -> (3,3)
        chainFacility.Add(chainMap, new Cell(3, 3), Direction.Right, 2, out _);  // #3 -> (5,3)

        LauncherChainResult forced = chainFacility.ResolveChain(chainMap, new Cell(1, 1));
        Check(forced.Success && forced.FinalCell == new Cell(5, 3)
              && forced.TriggeredLauncherIds.SequenceEqual(new[] { 1, 2, 3 })
              && forced.LandingPositions.SequenceEqual(new[]
              {
                  new Cell(3, 1), new Cell(3, 3), new Cell(5, 3)
              })
              && forced.GeometricDistance == 6,
            "launcher 6: A->B->C->plain forces B and C after one choice");

        var forcedJumps = chainFacility.TransitionsFrom(chainMap, new Cell(1, 2));
        Check(forcedJumps.Count == 1 && forcedJumps[0].Destination == new Cell(5, 3)
              && forcedJumps[0].TriggeredLauncherIds is { } forcedIds
              && forcedIds.SequenceEqual(new[] { 1, 2, 3 }),
            "launcher 6: one published move already contains the whole chain");

        SearchResult chainSteps = new ShortestStepsPathfinder { Probe = chainProbe }.Search(chainMap);
        Check(chainSteps.Found && chainSteps.Path.Contains(new Cell(5, 3)),
            "launcher 6: BFS reaches the forced final landing");
        LauncherPathPlan chainPlan = LauncherPathPlan.Attach(chainMap, chainProbe, chainSteps);
        Check(chainPlan.Resolved && chainSteps.LauncherJumps.Count == 1
              && chainSteps.LauncherJumps[0].TriggeredLauncherIds.SequenceEqual(new[] { 1, 2, 3 }),
            "launcher 6: the result keeps every forced pad, not only the first");

        // Test 7: a blocked landing late in the chain rejects the whole move.
        GridMap lateBlockMap = BuildOpenMap(9, 5, new Cell(0, 4), new Cell(8, 4));
        var lateBlockFacility = new DirectionalLauncherFacility();
        IPathProbe lateBlockProbe = lateBlockFacility.CreateProbe(lateBlockMap);
        lateBlockFacility.Add(lateBlockMap, new Cell(1, 1), Direction.Right, 2, out _); // -> (3,1)
        lateBlockFacility.Add(lateBlockMap, new Cell(3, 1), Direction.Right, 2, out _); // -> (5,1)
        lateBlockFacility.Add(lateBlockMap, new Cell(5, 1), Direction.Right, 2, out _); // -> (7,1)
        lateBlockMap.SetCost(new Cell(7, 1), GridMap.Wall);

        LauncherChainResult lateBlock = lateBlockFacility.ResolveChain(lateBlockMap, new Cell(1, 1));
        Check(!lateBlock.Success && lateBlock.Error == LauncherChainError.Blocked
              && lateBlock.TriggeredLauncherIds.SequenceEqual(new[] { 1, 2, 3 }),
            "launcher 7: a blocked third landing rejects the whole chain");
        Check(lateBlockFacility.TransitionsFrom(lateBlockMap, new Cell(1, 0)).Count == 0,
            "launcher 7: no partial chain is offered as a move");

        SearchResult lateBlockSteps =
            new ShortestStepsPathfinder { Probe = lateBlockProbe }.Search(lateBlockMap);
        Check(lateBlockSteps.Found && LauncherPathIsLegal(lateBlockMap, lateBlockFacility, lateBlockSteps.Path)
              && LauncherPathPlan.Attach(lateBlockMap, lateBlockProbe, lateBlockSteps).Jumps.Count == 0,
            "launcher 7: the route falls back to walking, never a half chain");

        // Test 8: a cycle is detected and rejected, not capped by an arbitrary limit.
        GridMap cycleMap = BuildOpenMap(9, 5, new Cell(0, 4), new Cell(8, 4));
        var cycleFacility = new DirectionalLauncherFacility();
        IPathProbe cycleProbe = cycleFacility.CreateProbe(cycleMap);
        cycleFacility.Add(cycleMap, new Cell(1, 1), Direction.Right, 2, out _); // -> (3,1)
        cycleFacility.Add(cycleMap, new Cell(3, 1), Direction.Down, 2, out _);  // -> (3,3)
        cycleFacility.Add(cycleMap, new Cell(3, 3), Direction.Left, 2, out _);  // -> (1,3)
        cycleFacility.Add(cycleMap, new Cell(1, 3), Direction.Up, 2, out _);    // -> (1,1)

        LauncherChainResult cycleChain = cycleFacility.ResolveChain(cycleMap, new Cell(1, 1));
        Check(!cycleChain.Success && cycleChain.Error == LauncherChainError.Cycle
              && cycleChain.TriggeredLauncherIds.SequenceEqual(new[] { 1, 2, 3, 4 }),
            "launcher 8: A->B->C->D->A is reported as a cycle");
        Check(cycleFacility.TransitionsFrom(cycleMap, new Cell(1, 2)).Count == 0,
            "launcher 8: a cyclic chain never becomes a path candidate");

        SearchResult cycleSteps = new ShortestStepsPathfinder { Probe = cycleProbe }.Search(cycleMap);
        Check(cycleSteps.Found && LauncherPathIsLegal(cycleMap, cycleFacility, cycleSteps.Path)
              && LauncherPathPlan.Attach(cycleMap, cycleProbe, cycleSteps).Jumps.Count == 0,
            "launcher 8: cyclic pads do not block ordinary walking");

        // Test 9: a disabled pad on the landing is ordinary terrain, not a chain node.
        GridMap offMap = BuildOpenMap(9, 3, new Cell(0, 0), new Cell(8, 2));
        var offFacility = new DirectionalLauncherFacility();
        IPathProbe offProbe = offFacility.CreateProbe(offMap);
        offFacility.Add(offMap, new Cell(1, 1), Direction.Right, 2, out _); // -> (3,1)
        offFacility.Add(offMap, new Cell(3, 1), Direction.Right, 2, out _); // -> (5,1)

        Check(offFacility.SetEnabled(2, false), "launcher 9: the second pad can be disabled");
        LauncherChainResult offChain = offFacility.ResolveChain(offMap, new Cell(1, 1));
        Check(offChain.Success && offChain.FinalCell == new Cell(3, 1)
              && offChain.TriggeredLauncherIds.SequenceEqual(new[] { 1 }),
            "launcher 9: a disabled pad ends the chain instead of firing");
        Check(offProbe.SpecialTransitions(new Cell(1, 2)).Any(jump => jump.Destination == new Cell(3, 1)),
            "launcher 9: the disabled pad cell is itself a legal landing");

        offMap.SetCost(new Cell(3, 1), GridMap.Wall);
        Check(!offFacility.ResolveChain(offMap, new Cell(1, 1)).Success
              && offProbe.SpecialTransitions(new Cell(1, 2)).Count() == 0,
            "launcher 9: a disabled pad on a wall blocks the chain like any obstacle");

        offMap.SetCost(new Cell(3, 1), GridMap.MinCost);
        Check(offFacility.SetEnabled(2, true)
              && offFacility.ResolveChain(offMap, new Cell(1, 1)).TriggeredLauncherIds
                  .SequenceEqual(new[] { 1, 2 }),
            "launcher 9: re-enabling the pad restores the forced chain");
        Check(offFacility.SetEnabled(1, false)
              && offProbe.SpecialTransitions(new Cell(1, 2)).Count() == 0,
            "launcher 9: a disabled entrance pad offers no move at all");

        // Test 10: a landing outside the map is never used.
        GridMap edgeLaunchMap = BuildOpenMap(9, 5, new Cell(0, 0), new Cell(8, 4));
        var edgeLaunchFacility = new DirectionalLauncherFacility();
        IPathProbe edgeLaunchProbe = edgeLaunchFacility.CreateProbe(edgeLaunchMap);
        edgeLaunchFacility.Add(edgeLaunchMap, new Cell(8, 2), Direction.Right, 2, out _);

        LauncherChainResult edgeChain = edgeLaunchFacility.ResolveChain(edgeLaunchMap, new Cell(8, 2));
        Check(!edgeChain.Success && edgeChain.Error == LauncherChainError.OutOfBounds
              && edgeChain.FinalCell is null && edgeChain.LandingPositions.Count == 0,
            "launcher 10: an out-of-bounds landing is rejected without storing a bad cell");
        Check(edgeLaunchProbe.SpecialTransitions(new Cell(7, 2)).Count() == 0,
            "launcher 10: no jump is published toward the map border");

        bool everyLandingInside = true;
        for (int y = 0; y < edgeLaunchMap.Height; y++)
        for (int x = 0; x < edgeLaunchMap.Width; x++)
            foreach (SpecialTransition jump in edgeLaunchProbe.SpecialTransitions(new Cell(x, y)))
                if (!edgeLaunchMap.InBounds(jump.Destination) || edgeLaunchMap.IsWall(jump.Destination))
                    everyLandingInside = false;
        Check(everyLandingInside, "launcher 10: every published landing stays inside the map");

        // Test 11: the shared probe serves every algorithm; ordinary routes still win.
        GridMap noProfitMap = BuildOpenMap(5, 3, new Cell(0, 1), new Cell(4, 1));
        var noProfitFacility = new DirectionalLauncherFacility();
        IPathProbe noProfitProbe = noProfitFacility.CreateProbe(noProfitMap);
        noProfitFacility.Add(noProfitMap, new Cell(2, 1), Direction.Up, 2, out _); // target off-map

        SearchResult plainRoute = new ShortestStepsPathfinder().Search(noProfitMap);
        SearchResult noProfitSteps = new ShortestStepsPathfinder { Probe = noProfitProbe }.Search(noProfitMap);
        SearchResult noProfitCost = new MinCostPathfinder { Probe = noProfitProbe }.Search(noProfitMap);
        Check(noProfitSteps.Found && SamePath(noProfitSteps.Path, plainRoute.Path)
              && LauncherPathPlan.Attach(noProfitMap, noProfitProbe, noProfitSteps).Jumps.Count == 0,
            "launcher 11: an unusable pad leaves the ordinary BFS route unchanged");
        Check(noProfitCost.Found && noProfitCost.Cost == plainRoute.Cost,
            "launcher 11: the ordinary Dijkstra price is unchanged too");

        SearchResult useCost = new MinCostPathfinder { Probe = useProbe }.Search(useMap);
        Check(useCost.Found && useCost.Path.Count == 4
              && LauncherPathPlan.Attach(useMap, useProbe, useCost).Jumps.Count == 1,
            "launcher 11: Dijkstra also picks a jump when it beats walking");
        Check(useSteps.Path.Count < plainRoute.Path.Count + 4
              && useCost.Cost == SumPathCost(useMap, useCost.Path),
            "launcher 11: the jump route keeps a consistent move count and price");

        // A sweep over every discovered module: each non-adjacent step must be a real chain.
        GridMap sweepLaunchMap = BuildOpenMap(7, 7, new Cell(0, 3), new Cell(6, 3));
        var sweepLaunchFacility = new DirectionalLauncherFacility();
        IPathProbe sweepLaunchProbe = sweepLaunchFacility.CreateProbe(sweepLaunchMap);
        sweepLaunchFacility.Add(sweepLaunchMap, new Cell(2, 3), Direction.Right, 3, out _); // -> (5,3)
        sweepLaunchFacility.Add(sweepLaunchMap, new Cell(1, 1), Direction.Right, 2, out _); // -> (3,1)
        sweepLaunchFacility.Add(sweepLaunchMap, new Cell(3, 1), Direction.Down, 2, out _);  // -> (3,3) cycle below
        sweepLaunchFacility.Add(sweepLaunchMap, new Cell(3, 3), Direction.Left, 2, out _);  // -> (1,3)
        sweepLaunchFacility.Add(sweepLaunchMap, new Cell(1, 3), Direction.Up, 2, out _);    // -> (1,1) cycle

        bool launcherSweepLegal = true;
        foreach (IPathfinder template in algorithms)
        {
            IPathfinder module = AlgorithmHost.Create(template);
            if (module is IReferencePointModule sweepReference)
                sweepReference.ReferencePoint = ObstacleCluster.FirstWall(sweepLaunchMap) ?? sweepLaunchMap.Goal;
            if (module is ITransitionAware sweepAware) sweepAware.Probe = sweepLaunchProbe;

            SearchResult sweepResult = module.Search(sweepLaunchMap);
            if (sweepResult.Found && !LauncherPathIsLegal(sweepLaunchMap, sweepLaunchFacility, sweepResult.Path))
            {
                launcherSweepLegal = false;
                Console.WriteLine($"  {template.Id} produced a route that fires an illegal launcher move");
            }
        }
        Check(launcherSweepLegal,
            "launcher 11: every discovered module keeps launcher moves legal");

        // Test 12: the result carries a replayable event chain, landing by landing.
        bool replayed = chainSteps.LauncherJumps.Count == 1;
        if (replayed)
        {
            LauncherJump jump = chainSteps.LauncherJumps[0];
            DirectionalLauncher firstPad = chainFacility.Launchers.First(pad => pad.Id == jump.TriggeredLauncherIds[0]);
            LauncherChainResult fresh = chainFacility.ResolveChain(chainMap, firstPad);

            replayed = fresh.Success && fresh.FinalCell == jump.Destination
                       && fresh.TriggeredLauncherIds.SequenceEqual(jump.TriggeredLauncherIds)
                       && fresh.LandingPositions.SequenceEqual(jump.LandingPositions)
                       && jump.LandingPositions.Count == 3;

            Cell walker = chainMap.Start;
            for (int index = 1; index < chainSteps.Path.Count; index++)
            {
                Cell next = chainSteps.Path[index];
                if (!AdjacentCells(walker, next)
                    && !chainSteps.LauncherJumps.Any(move => move.From == walker && move.Destination == next))
                    replayed = false;

                walker = next;
            }

            Check(walker == chainMap.Goal && replayed,
                "launcher 12: replaying the recorded pads lands on the same final cell");
            Check(LauncherPathIsLegal(chainMap, chainFacility, chainSteps.Path),
                "launcher 12: the executed route stays legal end to end");
        }
        else
        {
            Check(false, "launcher 12: the chain route contains no launcher event");
        }

        // Facility bookkeeping: placement guards, removal, reset and probe rebinding.
        var bookFacility = new DirectionalLauncherFacility();
        GridMap bookMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        Check(!bookFacility.Add(bookMap, new Cell(9, 9), Direction.Right, 2, out _),
            "launcher rules: out-of-bounds placement is refused");
        Check(!bookFacility.Add(bookMap, bookMap.Start, Direction.Right, 2, out _),
            "launcher rules: a gate cannot host a pad");
        Check(!bookFacility.Add(bookMap, new Cell(1, 1), Direction.Right, 0, out _),
            "launcher rules: a zero distance is refused");
        Check(bookFacility.Add(bookMap, new Cell(1, 1), Direction.Right, 2, out _),
            "launcher rules: a legal pad is accepted");
        Check(!bookFacility.Add(bookMap, new Cell(1, 1), Direction.Right, 2, out _),
            "launcher rules: the same cell cannot host two pads");
        Check(bookFacility.RemoveAt(new Cell(1, 1)) && bookFacility.Launchers.Count == 0,
            "launcher rules: removal forgets the pad and leaves the map alone");
        bookFacility.Add(bookMap, new Cell(1, 1), Direction.Right, 2, out _);
        bookFacility.Reset();
        Check(bookFacility.Launchers.Count == 0, "launcher rules: reset forgets every pad");

        Check(useProbe.For(useMap.Clone()).SpecialTransitions(new Cell(1, 1)).Count() == 1,
            "launcher probe: a rebinding clone sees the same pads");

        // ---- single-use taunt points: a mandatory-visit constraint, not a mover ----

        // Test 1: no point at all - every module behaves exactly as before.
        GridMap tauntOpenMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var tauntEmptyFacility = new TauntPointFacility();
        TauntPlan noTaunts = TauntPlan.Build(tauntOpenMap, null, tauntEmptyFacility.EnabledPoints());
        Check(!noTaunts.HasAny && noTaunts.FullMask == 0,
            "taunt 1: an empty facility produces an empty plan");

        SearchResult tauntPlainSteps = new ShortestStepsPathfinder().Search(tauntOpenMap);
        SearchResult tauntPlainCost = new MinCostPathfinder().Search(tauntOpenMap);
        SearchResult tauntEmptySteps = new ShortestStepsPathfinder { Taunt = noTaunts }.Search(tauntOpenMap);
        SearchResult tauntEmptyCost = new MinCostPathfinder { Taunt = noTaunts }.Search(tauntOpenMap);
        Check(SamePath(tauntEmptySteps.Path, tauntPlainSteps.Path)
              && tauntEmptySteps.Cost == tauntPlainSteps.Cost,
            "taunt 1: BFS is unchanged without points");
        Check(SamePath(tauntEmptyCost.Path, tauntPlainCost.Path)
              && tauntEmptyCost.Cost == tauntPlainCost.Cost,
            "taunt 1: Dijkstra is unchanged without points");

        // Test 2: one effective point is mandatory; one that cannot join a route is not.
        var tauntSingleFacility = new TauntPointFacility();
        Check(tauntSingleFacility.Add(tauntOpenMap, new Cell(2, 2), out _),
            "taunt 2: placing a point succeeds");
        TauntPlan tauntSinglePlan =
            TauntPlan.Build(tauntOpenMap, null, tauntSingleFacility.EnabledPoints());

        Check(tauntSinglePlan.HasAny && tauntSinglePlan.Effective.Count == 1,
            "taunt 2: the reachable point joins the effective set");
        SearchResult tauntSingleSteps =
            new ShortestStepsPathfinder { Taunt = tauntSinglePlan }.Search(tauntOpenMap);
        Check(tauntSingleSteps.Found && tauntSingleSteps.Path.Contains(new Cell(2, 2)),
            "taunt 2: BFS passes through the effective point");
        Check(tauntSingleSteps.TauntRequiredCount == 1 && tauntSingleSteps.TauntVisitedCount == 1,
            "taunt 2: BFS reports 1/1 visited points");

        SearchResult tauntSingleCost =
            new MinCostPathfinder { Taunt = tauntSinglePlan }.Search(tauntOpenMap);
        Check(tauntSingleCost.Found && tauntSingleCost.Path.Contains(new Cell(2, 2))
              && tauntSingleCost.TauntVisitedCount == 1,
            "taunt 2: Dijkstra passes through the effective point too");

        // A sealed point cannot join any route, so it must not become a requirement.
        GridMap tauntSealedMap = BuildOpenMap(5, 5, new Cell(0, 4), new Cell(4, 4));
        tauntSealedMap.SetCost(new Cell(0, 1), GridMap.Wall);
        tauntSealedMap.SetCost(new Cell(2, 1), GridMap.Wall);
        tauntSealedMap.SetCost(new Cell(1, 0), GridMap.Wall);
        tauntSealedMap.SetCost(new Cell(1, 2), GridMap.Wall);
        var tauntSealedFacility = new TauntPointFacility();
        tauntSealedFacility.Add(tauntSealedMap, new Cell(1, 1), out _);
        TauntPlan tauntSealedPlan =
            TauntPlan.Build(tauntSealedMap, null, tauntSealedFacility.EnabledPoints());
        Check(!tauntSealedPlan.HasAny,
            "taunt 2: a walled-in point is never mandatory");

        // Test 3: several effective points must all appear on the same route.
        GridMap tauntMultiMap = BuildOpenMap(7, 7, new Cell(0, 0), new Cell(6, 6));
        var tauntMultiFacility = new TauntPointFacility();
        tauntMultiFacility.Add(tauntMultiMap, new Cell(1, 5), out _);
        tauntMultiFacility.Add(tauntMultiMap, new Cell(5, 1), out _);
        tauntMultiFacility.Add(tauntMultiMap, new Cell(3, 3), out _);
        TauntPlan tauntMultiPlan =
            TauntPlan.Build(tauntMultiMap, null, tauntMultiFacility.EnabledPoints());

        Check(tauntMultiPlan.Effective.Count == 3,
            "taunt 3: all three open-map points are effective");
        SearchResult tauntMultiSteps =
            new ShortestStepsPathfinder { Taunt = tauntMultiPlan }.Search(tauntMultiMap);
        Check(tauntMultiSteps.Found && VisitsAllTaunts(tauntMultiSteps.Path, tauntMultiPlan),
            "taunt 3: BFS visits every effective point on one route");
        SearchResult tauntMultiCost =
            new MinCostPathfinder { Taunt = tauntMultiPlan }.Search(tauntMultiMap);
        Check(tauntMultiCost.Found && VisitsAllTaunts(tauntMultiCost.Path, tauntMultiPlan),
            "taunt 3: Dijkstra visits every effective point on one route");

        // Test 4: visiting once is enough; a point on a dead-end branch may be revisited
        // by the walk, but its bit is only set once.
        GridMap tauntRevisitMap = BuildOpenMap(5, 3, new Cell(0, 1), new Cell(4, 1));
        for (int x = 0; x < 5; x++)
        {
            tauntRevisitMap.SetCost(new Cell(x, 0), GridMap.Wall);
            tauntRevisitMap.SetCost(new Cell(x, 2), GridMap.Wall);
        }

        tauntRevisitMap.SetCost(new Cell(1, 0), GridMap.MinCost);
        var tauntRevisitFacility = new TauntPointFacility();
        tauntRevisitFacility.Add(tauntRevisitMap, new Cell(1, 0), out _);
        TauntPlan tauntRevisitPlan =
            TauntPlan.Build(tauntRevisitMap, null, tauntRevisitFacility.EnabledPoints());

        SearchResult tauntRevisitSteps =
            new ShortestStepsPathfinder { Taunt = tauntRevisitPlan }.Search(tauntRevisitMap);
        Check(tauntRevisitSteps.Found && tauntRevisitSteps.Path.Contains(new Cell(1, 0)),
            "taunt 4: the dead-end point is visited");
        Check(tauntRevisitSteps.Path.Count(cell => cell == new Cell(1, 1)) == 2,
            "taunt 4: the branch cell is crossed twice without repeating the requirement");
        Check(tauntRevisitSteps.TauntVisitedCount == 1,
            "taunt 4: the point counts as visited exactly once");

        SearchResult tauntRevisitCost =
            new MinCostPathfinder { Taunt = tauntRevisitPlan }.Search(tauntRevisitMap);
        Check(tauntRevisitCost.Found && tauntRevisitCost.Path.Contains(new Cell(1, 0))
              && tauntRevisitCost.TauntVisitedCount == 1,
            "taunt 4: Dijkstra also reaches the dead-end point once");

        // Test 5: a blocked point is skipped, while a reachable one is still forced.
        var tauntMixedFacility = new TauntPointFacility();
        tauntMixedFacility.Add(tauntSealedMap, new Cell(1, 1), out _); // sealed
        tauntMixedFacility.Add(tauntSealedMap, new Cell(2, 4), out _); // on the main row
        TauntPlan tauntMixedPlan =
            TauntPlan.Build(tauntSealedMap, null, tauntMixedFacility.EnabledPoints());
        Check(tauntMixedPlan.Effective.Count == 1,
            "taunt 5: only the reachable point survives the filter");
        SearchResult tauntMixedSteps =
            new ShortestStepsPathfinder { Taunt = tauntMixedPlan }.Search(tauntSealedMap);
        Check(tauntMixedSteps.Found && tauntMixedSteps.Path.Contains(new Cell(2, 4))
              && !tauntMixedSteps.Path.Contains(new Cell(1, 1)),
            "taunt 5: the sealed point is ignored, the reachable one is visited");

        // Test 6: reachable from the start but unable to reach the goal -> not effective.
        GridMap tauntTrapMap = BuildOpenMap(5, 3, new Cell(0, 1), new Cell(4, 1));
        tauntTrapMap.SetCost(new Cell(2, 0), GridMap.Wall); // only the dead-end side entry remains
        var tauntTrapOneWay = new OneWayFacility();
        tauntTrapOneWay.Add(tauntTrapMap, new Cell(1, 0), Direction.Left, out _);
        IPathProbe tauntTrapProbe = tauntTrapOneWay.ApplyTo(OneWayFacility.Plain(tauntTrapMap));
        var tauntTrapFacility = new TauntPointFacility();
        tauntTrapFacility.Add(tauntTrapMap, new Cell(1, 0), out _);
        TauntPlan tauntTrapPlan =
            TauntPlan.Build(tauntTrapMap, tauntTrapProbe, tauntTrapFacility.EnabledPoints());

        Check(!tauntTrapPlan.HasAny,
            "taunt 6: a reachable dead end is not part of any start-to-goal route");
        SearchResult tauntTrapSteps = new ShortestStepsPathfinder
        {
            Probe = tauntTrapProbe,
            Taunt = tauntTrapPlan
        }.Search(tauntTrapMap);
        Check(tauntTrapSteps.Found && !tauntTrapSteps.Path.Contains(new Cell(1, 0)),
            "taunt 6: the search keeps its plain route and never enters the trap");

        // Test 7: each point is reachable on its own, but no single route can visit both.
        // Two one-way rows leave the start and both end at a sink goal; collecting one
        // row commits the walk to the goal, so the other row can never be visited.
        GridMap tauntForkMap = BuildOpenMap(6, 5, new Cell(0, 2), new Cell(5, 2));
        for (int x = 0; x < 6; x++)
        {
            tauntForkMap.SetCost(new Cell(x, 0), GridMap.Wall);
            tauntForkMap.SetCost(new Cell(x, 4), GridMap.Wall);
        }

        for (int x = 1; x <= 4; x++)
            tauntForkMap.SetCost(new Cell(x, 2), GridMap.Wall);

        var tauntForkOneWay = new OneWayFacility();
        for (int x = 1; x <= 4; x++)
        {
            tauntForkOneWay.Add(tauntForkMap, new Cell(x, 1), Direction.Right, out _);
            tauntForkOneWay.Add(tauntForkMap, new Cell(x, 3), Direction.Right, out _);
        }

        IPathProbe tauntForkProbe = tauntForkOneWay.ApplyTo(OneWayFacility.Plain(tauntForkMap));
        var tauntForkFacility = new TauntPointFacility();
        tauntForkFacility.Add(tauntForkMap, new Cell(1, 1), out _); // top row
        tauntForkFacility.Add(tauntForkMap, new Cell(1, 3), out _); // bottom row
        TauntPlan tauntForkPlan =
            TauntPlan.Build(tauntForkMap, tauntForkProbe, tauntForkFacility.EnabledPoints());

        Check(tauntForkPlan.Effective.Count == 2,
            "taunt 7: both points are individually on a legal route");
        SearchResult tauntForkSteps = new ShortestStepsPathfinder
        {
            Probe = tauntForkProbe,
            Taunt = tauntForkPlan
        }.Search(tauntForkMap);
        Check(!tauntForkSteps.Found && tauntForkSteps.Note is not null
              && tauntForkSteps.Note.Contains("嘲讽点"),
            "taunt 7: jointly unreachable points make the whole search fail with a reason");
        Check(tauntForkSteps.TauntRequiredCount == 2,
            "taunt 7: the failure still reports the two required points");

        SearchResult tauntForkCost = new MinCostPathfinder
        {
            Probe = tauntForkProbe,
            Taunt = tauntForkPlan
        }.Search(tauntForkMap);
        Check(!tauntForkCost.Found,
            "taunt 7: Dijkstra also refuses the impossible joint route");

        // Test 8a: a taunt point reachable only through a launcher landing.
        GridMap tauntLaunchMap = BuildOpenMap(7, 3, new Cell(0, 1), new Cell(6, 1));
        var tauntLaunchFacility = new DirectionalLauncherFacility();
        tauntLaunchFacility.Add(tauntLaunchMap, new Cell(2, 1), Direction.Right, 3, out _);
        tauntLaunchMap.SetCost(new Cell(3, 1), GridMap.Wall);
        tauntLaunchMap.SetCost(new Cell(4, 1), GridMap.Wall);
        tauntLaunchMap.SetCost(new Cell(5, 0), GridMap.Wall);
        tauntLaunchMap.SetCost(new Cell(5, 2), GridMap.Wall);
        IPathProbe tauntLaunchProbe = tauntLaunchFacility.CreateProbe(tauntLaunchMap);
        var tauntLaunchPoints = new TauntPointFacility();
        tauntLaunchPoints.Add(tauntLaunchMap, new Cell(5, 1), out _);
        TauntPlan tauntLaunchPlan =
            TauntPlan.Build(tauntLaunchMap, tauntLaunchProbe, tauntLaunchPoints.EnabledPoints());

        Check(tauntLaunchPlan.Effective.Count == 1,
            "taunt 8: the launcher landing is effective");
        SearchResult tauntLaunchSteps = new ShortestStepsPathfinder
        {
            Probe = tauntLaunchProbe,
            Taunt = tauntLaunchPlan
        }.Search(tauntLaunchMap);
        Check(tauntLaunchSteps.Found && tauntLaunchSteps.Path.Contains(new Cell(5, 1))
              && tauntLaunchSteps.TauntVisitedCount == 1,
            "taunt 8: a launcher landing marks the point as visited");
        bool tauntJumpedOntoPoint = false;
        for (int index = 1; index < tauntLaunchSteps.Path.Count; index++)
            if (tauntLaunchSteps.Path[index] == new Cell(5, 1)
                && !AdjacentCells(tauntLaunchSteps.Path[index - 1], tauntLaunchSteps.Path[index]))
                tauntJumpedOntoPoint = true;
        Check(tauntJumpedOntoPoint,
            "taunt 8: the route lands on the point through the jump edge");

        // Test 8b: a taunt point reachable only through a portal exit.
        GridMap tauntPortalMap = BuildOpenMap(7, 3, new Cell(0, 1), new Cell(6, 1));
        var tauntPortalFacility = new PortalFacility { TeleportCost = 0 };
        tauntPortalFacility.Add(tauntPortalMap, new Cell(3, 0), out _);
        tauntPortalFacility.Add(tauntPortalMap, new Cell(3, 2), out _);
        tauntPortalMap.SetCost(new Cell(3, 1), GridMap.Wall);
        IPathProbe tauntPortalProbe = tauntPortalFacility.CreateProbe(tauntPortalMap);
        var tauntPortalPoints = new TauntPointFacility();
        tauntPortalPoints.Add(tauntPortalMap, new Cell(4, 2), out _);
        TauntPlan tauntPortalPlan =
            TauntPlan.Build(tauntPortalMap, tauntPortalProbe, tauntPortalPoints.EnabledPoints());

        Check(tauntPortalPlan.Effective.Count == 1,
            "taunt 8: the portal exit is effective");
        SearchResult tauntPortalSteps = new ShortestStepsPathfinder
        {
            Probe = tauntPortalProbe,
            Taunt = tauntPortalPlan
        }.Search(tauntPortalMap);
        Check(tauntPortalSteps.Found && tauntPortalSteps.Path.Contains(new Cell(4, 2))
              && tauntPortalSteps.TauntVisitedCount == 1,
            "taunt 8: a teleport landing marks the point as visited");
        Check(PortalPathIsLegal(tauntPortalMap, tauntPortalFacility, tauntPortalSteps.Path),
            "taunt 8: the taunt-constrained route still obeys the portal rules");

        // Test 8c: a taunt point on a one-way corridor still respects the arrow.
        GridMap tauntArrowMap = BuildOpenMap(7, 3, new Cell(0, 1), new Cell(6, 1));
        var tauntArrowFacility = new OneWayFacility();
        tauntArrowFacility.Add(tauntArrowMap, new Cell(2, 1), Direction.Right, out _);
        tauntArrowFacility.Add(tauntArrowMap, new Cell(4, 1), Direction.Right, out _);
        IPathProbe tauntArrowProbe = tauntArrowFacility.ApplyTo(OneWayFacility.Plain(tauntArrowMap));
        var tauntArrowPoints = new TauntPointFacility();
        tauntArrowPoints.Add(tauntArrowMap, new Cell(3, 1), out _);
        TauntPlan tauntArrowPlan =
            TauntPlan.Build(tauntArrowMap, tauntArrowProbe, tauntArrowPoints.EnabledPoints());

        Check(tauntArrowPlan.Effective.Count == 1,
            "taunt 8: the corridor point is effective");
        SearchResult tauntArrowSteps = new ShortestStepsPathfinder
        {
            Probe = tauntArrowProbe,
            Taunt = tauntArrowPlan
        }.Search(tauntArrowMap);
        Check(tauntArrowSteps.Found && tauntArrowSteps.Path.Contains(new Cell(3, 1))
              && OneWayPathIsLegal(tauntArrowMap, tauntArrowFacility, tauntArrowSteps.Path),
            "taunt 8: the visit never breaks the one-way rule");

        // Test 9: every edit is visible to the next plan and the next search.
        GridMap tauntDynamicMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        var tauntDynamicFacility = new TauntPointFacility();
        tauntDynamicFacility.Add(tauntDynamicMap, new Cell(2, 2), out _);
        TauntPoint tauntDynamicPoint = tauntDynamicFacility.At(new Cell(2, 2))!;

        TauntPlan tauntLivePlan =
            TauntPlan.Build(tauntDynamicMap, null, tauntDynamicFacility.EnabledPoints());
        Check(tauntLivePlan.HasAny, "taunt 9: a fresh point is effective");

        Check(tauntDynamicFacility.SetEnabled(tauntDynamicPoint.Id, false),
            "taunt 9: the point can be disabled");
        TauntPlan tauntOffPlan =
            TauntPlan.Build(tauntDynamicMap, null, tauntDynamicFacility.EnabledPoints());
        Check(!tauntOffPlan.HasAny, "taunt 9: a disabled point is dropped immediately");
        SearchResult tauntOffSteps =
            new ShortestStepsPathfinder { Taunt = tauntOffPlan }.Search(tauntDynamicMap);
        Check(tauntOffSteps.Found && tauntOffSteps.TauntRequiredCount == 0,
            "taunt 9: the next search no longer forces the disabled point");

        tauntDynamicFacility.SetEnabled(tauntDynamicPoint.Id, true);
        tauntDynamicFacility.Add(tauntDynamicMap, new Cell(1, 3), out _);
        TauntPlan tauntBackPlan =
            TauntPlan.Build(tauntDynamicMap, null, tauntDynamicFacility.EnabledPoints());
        Check(tauntBackPlan.Effective.Count == 2,
            "taunt 9: re-enabling and adding a point are both seen at once");
        SearchResult tauntBackSteps =
            new ShortestStepsPathfinder { Taunt = tauntBackPlan }.Search(tauntDynamicMap);
        Check(tauntBackSteps.Found && VisitsAllTaunts(tauntBackSteps.Path, tauntBackPlan),
            "taunt 9: the search visits the updated point set");

        tauntDynamicFacility.RemoveAt(new Cell(2, 2));
        tauntDynamicFacility.RemoveAt(new Cell(1, 3));
        TauntPlan tauntGonePlan =
            TauntPlan.Build(tauntDynamicMap, null, tauntDynamicFacility.EnabledPoints());
        Check(!tauntGonePlan.HasAny, "taunt 9: removing the points clears the requirement");

        // The score module's simple walk must also collect every effective point.
        SearchResult tauntScoreRoute = new MaxPerpendicularPathfinder { Taunt = tauntSinglePlan }
            .Search(tauntOpenMap);
        Check(tauntScoreRoute.Found && tauntScoreRoute.Path.Contains(new Cell(2, 2))
              && tauntScoreRoute.TauntVisitedCount == 1,
            "taunt 9: 06 keeps its route and visits the point");

        // 07's lexicographic route honours the constraint and allows a legitimate repeat.
        SearchResult tauntReferenceRoute = new MinimumReferenceTraversalPathfinder
        {
            Taunt = tauntRevisitPlan
        }.Search(tauntRevisitMap);
        Check(tauntReferenceRoute.Found && tauntReferenceRoute.Path.Contains(new Cell(1, 0))
              && tauntReferenceRoute.TauntVisitedCount == 1,
            "taunt 9: 07 visits the dead-end point once");

        // Placement guards and facility bookkeeping.
        var tauntBookFacility = new TauntPointFacility();
        GridMap tauntBookMap = BuildOpenMap(5, 5, new Cell(0, 0), new Cell(4, 4));
        Check(!tauntBookFacility.Add(tauntBookMap, new Cell(9, 9), out _),
            "taunt rules: out-of-bounds placement is refused");
        Check(!tauntBookFacility.Add(tauntBookMap, tauntBookMap.Start, out _),
            "taunt rules: a gate cannot host a point");
        Check(tauntBookFacility.Add(tauntBookMap, new Cell(1, 1), out _),
            "taunt rules: a legal point is accepted");
        Check(!tauntBookFacility.Add(tauntBookMap, new Cell(1, 1), out _),
            "taunt rules: the same cell cannot host two points");
        Check(tauntBookFacility.RemoveAt(new Cell(1, 1)) && tauntBookFacility.Points.Count == 0,
            "taunt rules: removal forgets the point and leaves the map alone");
        tauntBookFacility.Add(tauntBookMap, new Cell(1, 1), out _);
        tauntBookFacility.Reset();
        Check(tauntBookFacility.Points.Count == 0, "taunt rules: reset forgets every point");

        bool allConnected = true;
        for (int seed = 1; seed <= 25; seed++)
        {
            GridMap randomMap = GridMapFactory.CreateRandom(16, 16, seed);
            if (!IsReachable(randomMap))
            {
                allConnected = false;
                Console.WriteLine($"  internal map seed {seed} is unreachable");
                break;
            }
        }
        Check(allConnected, "internal random maps are connected (seeds 1..25)");

        var paintMap = new GridMap(5, 5) { Start = new Cell(0, 0), Goal = new Cell(4, 4) };
        paintMap.SetCost(paintMap.Start, GridMap.Wall);
        Check(!paintMap.IsWall(paintMap.Start), "start cell cannot be walled");
        paintMap.SetAllTraversableCost(7);
        Check(paintMap.CostAt(new Cell(2, 2)) == 7, "global empty-cell cost applied");
        paintMap.SetCost(new Cell(2, 2), GridMap.Wall);
        Check(paintMap.IsWall(new Cell(2, 2)), "wall painting works");
        paintMap.SetCost(new Cell(2, 2), GridMap.MinCost);
        Check(!paintMap.IsWall(new Cell(2, 2)), "wall removal works");

        if (GridMapFactory.TryCreateFromRmg(20261025, out GridMap rmgMap, out string note))
        {
            Console.WriteLine($"RMG map loaded ({note})");
            Check(rmgMap.Width == 16 && rmgMap.Height == 16, "RMG map is 16x16");
            Check(IsReachable(rmgMap), "RMG map is connected");

            if (algorithms.Count > 0)
            {
                SearchResult rmgResult = algorithms[0].Search(rmgMap);
                Check(rmgResult.Found, "RMG map has a path");
            }

            FarthestPointResult rmgFarthest = farthestFinder.Find(rmgMap, rmgMap.Start);
            Console.WriteLine($"  farthest from entrance on seed 20261025: "
                              + $"({rmgFarthest.Cell.X},{rmgFarthest.Cell.Y}) d={rmgFarthest.Distance}");
            Check(rmgFarthest.Found && rmgFarthest.Distance > 0, "farthest: RMG far point found");
            Check(MatchesReferenceDistances(rmgMap, rmgMap.Start, rmgFarthest),
                "farthest: RMG distances match an independent BFS");
        }
        else
        {
            Console.WriteLine($"SKIP  RMG integration ({note})");
        }

        Console.WriteLine(failures.Count == 0
            ? "SELFTEST PASS"
            : $"SELFTEST FAIL ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Direct 6-step bridge through price-9 cells versus a 10-step price-1 detour.
    /// The two objectives must produce different paths on this map.
    /// </summary>
    private static GridMap BuildTestMap()
    {
        var map = new GridMap(9, 9) { Start = new Cell(1, 4), Goal = new Cell(7, 4) };

        // Walls force the detour two rows away from the bridge.
        for (int x = 2; x <= 6; x++)
        {
            map.SetCost(new Cell(x, 3), GridMap.Wall);
            map.SetCost(new Cell(x, 5), GridMap.Wall);
        }

        // The bridge: six moves across, five of them at price 9.
        for (int x = 2; x <= 7; x++)
            map.SetCost(new Cell(x, 4), 9);

        map.SetCost(map.Start, GridMap.MinCost);
        map.SetCost(map.Goal, GridMap.MinCost);
        return map;
    }

    private static GridMap BuildOpenMap(int width, int height, Cell start, Cell goal)
    {
        return new GridMap(width, height) { Start = start, Goal = goal };
    }

    /// <summary>
    /// Independent oracle for the constrained route: run the 01 module once per ring cell
    /// in both directions and keep the cheapest total (ties: fewer moves, then row-major).
    /// </summary>
    private static (int Cost, Cell Via) ConstrainedOracle(GridMap map, Cell reference)
    {
        List<Cell> ring = ObstacleCluster.Ring(map, ObstacleCluster.Collect(map, reference));

        int bestCost = int.MaxValue;
        int bestSteps = int.MaxValue;
        Cell bestVia = default;

        foreach (Cell target in ring)
        {
            GridMap legIn = map.Clone();
            legIn.Goal = target;
            SearchResult first = new MinCostPathfinder().Search(legIn);

            GridMap legOut = map.Clone();
            legOut.Start = target;
            SearchResult second = new MinCostPathfinder().Search(legOut);

            if (!first.Found || !second.Found) continue;

            // 01 charges every entered cell and never charges the start cell, so leg 2
            // already leaves the join cell out; the two legs add up without a refund.
            int total = first.Cost + second.Cost;
            int steps = first.Path.Count + second.Path.Count - 2;

            if (total > bestCost) continue;
            if (total == bestCost && steps >= bestSteps) continue;

            bestCost = total;
            bestSteps = steps;
            bestVia = target;
        }

        return (bestCost, bestVia);
    }

    private static bool IsValidPath(GridMap map, List<Cell> path)
    {
        if (path.Count == 0) return false;
        if (path[0] != map.Start || path[^1] != map.Goal) return false;

        for (int i = 0; i < path.Count; i++)
        {
            if (!map.InBounds(path[i]) || map.IsWall(path[i])) return false;
            if (i == 0) continue;

            int dx = Math.Abs(path[i].X - path[i - 1].X);
            int dy = Math.Abs(path[i].Y - path[i - 1].Y);
            if (dx + dy != 1) return false;
        }

        return true;
    }

    private static int SumPathCost(GridMap map, List<Cell> path)
    {
        int total = 0;
        for (int i = 1; i < path.Count; i++) total += map.CostAt(path[i]);
        return total;
    }

    /// <summary>
    /// Independent oracle for the perpendicular route: enumerate every simple walk from
    /// the start gate by plain recursion and keep the highest score. No pruning and no
    /// shared code with the module, so it only makes sense on the small selftest boards.
    /// </summary>
    private static long PerpendicularOracle(GridMap map, Cell marker)
    {
        bool[,] visited = new bool[map.Width, map.Height];
        visited[map.Start.X, map.Start.Y] = true;
        long best = long.MinValue;

        void Walk(Cell current, long score)
        {
            // The goal can be visited once, so a walk that reaches it must end there.
            if (current == map.Goal)
            {
                if (score > best) best = score;
                return;
            }

            foreach (Cell next in map.Neighbours(current))
            {
                if (!map.InBounds(next) || map.IsWall(next)) continue;
                if (visited[next.X, next.Y]) continue;

                visited[next.X, next.Y] = true;
                Walk(next, score + PerpendicularWeight(current, next, marker));
                visited[next.X, next.Y] = false;
            }
        }

        Walk(map.Start, 0);
        return best;
    }

    /// <summary>One move's weight, derived from the definition rather than from the module.</summary>
    private static int PerpendicularWeight(Cell from, Cell to, Cell marker)
    {
        return to.Y == from.Y
            ? Math.Abs(to.Y - marker.Y)   // horizontal move: the row's distance to the marker row
            : Math.Abs(to.X - marker.X);  // vertical move: the column's distance to the marker column
    }

    /// <summary>Score of the plain shortest route, used as the seed baseline.</summary>
    private static long ShortestScore(GridMap map, Cell marker)
    {
        SearchResult shortest = new ShortestStepsPathfinder().Search(map);
        return shortest.Found
            ? MaxPerpendicularPathfinder.CalculatePathScore(shortest.Path, marker)
            : long.MinValue;
    }

    /// <summary>True when the route contains every effective taunt point, in any order.</summary>
    private static bool VisitsAllTaunts(IReadOnlyList<Cell>? path, TauntPlan plan)
    {
        if (path is null) return false;

        var visited = new HashSet<Cell>(path);
        foreach (TauntPoint point in plan.Effective)
            if (!visited.Contains(point.Position))
                return false;

        return true;
    }

    private static bool SamePath(IReadOnlyList<Cell>? left, IReadOnlyList<Cell>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;

        for (int i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;

        return true;
    }

    /// <summary>
    /// Independent oracle for the reference route: enumerate every simple walk from the
    /// start gate and keep the lexicographic minimum of (crossings, moves). No pruning,
    /// so it only suits the small selftest boards.
    /// </summary>
    private static (int Crossings, int Length) ReferenceTraversalOracle(
        GridMap map, IReadOnlyList<Cell>? references)
    {
        var referenceSet = new HashSet<Cell>();
        if (references is not null)
            foreach (Cell cell in references)
                if (map.InBounds(cell))
                    referenceSet.Add(cell);

        bool[,] visited = new bool[map.Width, map.Height];
        visited[map.Start.X, map.Start.Y] = true;
        int bestCrossings = int.MaxValue;
        int bestLength = int.MaxValue;

        void Walk(Cell current, int crossings, int moves)
        {
            if (current == map.Goal)
            {
                if (crossings < bestCrossings
                    || (crossings == bestCrossings && moves < bestLength))
                {
                    bestCrossings = crossings;
                    bestLength = moves;
                }

                return;
            }

            foreach (Cell next in map.Neighbours(current))
            {
                if (map.IsWall(next) || visited[next.X, next.Y]) continue;

                visited[next.X, next.Y] = true;
                int add = referenceSet.Contains(next) && next != map.Start && next != map.Goal ? 1 : 0;
                Walk(next, crossings + add, moves + 1);
                visited[next.X, next.Y] = false;
            }
        }

        Walk(map.Start, 0, 0);
        return (bestCrossings, bestLength);
    }

    /// <summary>
    /// Independent legality check for a portal route. Ordinary steps must be 4-neighbour
    /// moves; every jump has to leave a cell next to one enabled portal and land next to
    /// another enabled portal; no cell may leave the map, hit a wall or be a portal.
    /// <paramref name="requireSimple"/> false relaxes only the no-repeat rule, which the
    /// composite loiter route cannot always honour inside a one-way pocket.
    /// Reads only the facility's portal list, never its cached transitions.
    /// </summary>
    private static bool PortalPathIsLegal(GridMap map, PortalFacility facility, List<Cell> path,
        bool requireSimple = true)
    {
        if (path.Count == 0 || path[0] != map.Start || path[^1] != map.Goal) return false;

        IReadOnlyList<PortalFacility.Portal> portals = facility.Portals;
        var seen = new HashSet<Cell>();

        for (int index = 0; index < path.Count; index++)
        {
            Cell cell = path[index];
            if (!map.InBounds(cell) || map.IsWall(cell)) return false;
            if (portals.Any(portal => portal.Position == cell)) return false;
            if (requireSimple && !seen.Add(cell)) return false;
            if (index == 0) continue;

            Cell from = path[index - 1];
            if (AdjacentCells(from, cell)) continue;

            bool jump = portals.Any(source => source.Enabled
                                              && AdjacentCells(source.Position, from)
                                              && portals.Any(target => target.Enabled
                                                  && target.Id != source.Id
                                                  && AdjacentCells(target.Position, cell)));
            if (!jump) return false;
        }

        return true;
    }

    /// <summary>Re-walks a portal route and rebuilds its price from the map and the portal fees.</summary>
    private static int PortalPathCost(GridMap map, PortalFacility facility, List<Cell> path)
    {
        int total = 0;

        for (int index = 1; index < path.Count; index++)
        {
            Cell from = path[index - 1];
            Cell cell = path[index];
            total += map.CostAt(cell);

            if (AdjacentCells(from, cell)) continue;

            total += facility.Portals
                .Where(portal => AdjacentCells(portal.Position, from))
                .Select(portal => portal.TeleportCost)
                .DefaultIfEmpty(0)
                .First();
        }

        return total;
    }

    /// <summary>
    /// Re-walks a route over an arrow map: every step must stay in bounds, off walls,
    /// a 4-neighbour move, not one of the two forbidden edges of any passage, and no
    /// passage crossing may turn — a walked-in entry must leave straight through.
    /// </summary>
    private static bool OneWayPathIsLegal(GridMap map, OneWayFacility facility, List<Cell> path)
    {
        if (path.Count == 0 || path[0] != map.Start || path[^1] != map.Goal) return false;

        for (int index = 0; index < path.Count; index++)
        {
            Cell cell = path[index];
            if (!map.InBounds(cell) || map.IsWall(cell)) return false;
            if (index == 0) continue;

            Cell from = path[index - 1];
            if (!AdjacentCells(from, cell)) return false;
            if (facility.BlocksMove(from, cell)) return false;

            if (index + 1 < path.Count && facility.At(cell) is not null)
            {
                Cell next = path[index + 1];
                Cell straight = new Cell(2 * cell.X - from.X, 2 * cell.Y - from.Y);
                if (next != straight) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Independent launcher audit: every step is either a legal 4-neighbour walk or a
    /// published launcher chain whose final landing is the next path node. Crossed
    /// cells are never inspected, only landings.
    /// </summary>
    private static bool LauncherPathIsLegal(GridMap map, DirectionalLauncherFacility facility, List<Cell> path)
    {
        if (path.Count == 0 || path[0] != map.Start || path[^1] != map.Goal) return false;

        for (int index = 0; index < path.Count; index++)
        {
            Cell cell = path[index];
            if (!map.InBounds(cell) || map.IsWall(cell)) return false;
            if (index == 0) continue;

            Cell from = path[index - 1];
            if (AdjacentCells(from, cell)) continue;

            bool matched = false;
            foreach (SpecialTransition transition in facility.TransitionsFrom(map, from))
            {
                if (transition.Type != TransitionType.Launcher) continue;
                if (transition.Destination != cell) continue;

                matched = true;
                break;
            }

            if (!matched) return false;
        }

        return true;
    }

    /// <summary>First cell a route reaches by jumping, or the goal when it never jumps.</summary>
    private static Cell FirstJumpLanding(List<Cell> path)
    {
        for (int index = 1; index < path.Count; index++)
            if (!AdjacentCells(path[index - 1], path[index]))
                return path[index];

        return path[^1];
    }

    /// <summary>
    /// Independent re-derivation of every exit reachable from one entry cell:
    /// each enabled portal next to the entry offers all exits next to every other enabled
    /// portal, minus the entry cell itself (a jump must move).
    /// </summary>
    private static List<Cell> ExpectedPortalExits(GridMap map, PortalFacility facility, Cell entry)
    {
        IReadOnlyList<PortalFacility.Portal> portals = facility.Portals;
        var exits = new List<Cell>();

        foreach (PortalFacility.Portal source in portals)
        {
            if (!source.Enabled || !AdjacentCells(source.Position, entry)) continue;

            foreach (PortalFacility.Portal target in portals)
            {
                if (target.Id == source.Id || !target.Enabled) continue;

                foreach (Cell next in map.Neighbours(target.Position))
                {
                    if (map.IsWall(next)) continue;
                    if (portals.Any(portal => portal.Position == next)) continue;
                    if (next == entry) continue;
                    if (!exits.Contains(next)) exits.Add(next);
                }
            }
        }

        return exits.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
    }

    private static bool AdjacentCells(Cell left, Cell right)
        => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y) == 1;

    /// <summary>Independent O(V^2) Dijkstra used only as a test oracle.</summary>
    private static int ReferenceCost(GridMap map)
    {
        int count = map.Width * map.Height;
        int Index(Cell c) => c.Y * map.Width + c.X;

        var distance = new int[count];
        var done = new bool[count];
        for (int i = 0; i < count; i++) distance[i] = int.MaxValue;
        distance[Index(map.Start)] = 0;

        for (int iteration = 0; iteration < count; iteration++)
        {
            int current = -1;
            int best = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (done[i] || distance[i] >= best) continue;
                best = distance[i];
                current = i;
            }

            if (current < 0) break;
            done[current] = true;

            var cell = new Cell(current % map.Width, current / map.Width);
            foreach (Cell next in map.Neighbours(cell))
            {
                int step = map.CostAt(next);
                if (step == GridMap.Wall) continue;

                int index = Index(next);
                int candidate = distance[current] + step;
                if (candidate < distance[index]) distance[index] = candidate;
            }
        }

        return distance[Index(map.Goal)];
    }

    /// <summary>Independent BFS used only as a step-count oracle.</summary>
    private static int ReferenceSteps(GridMap map)
    {
        var distance = new Dictionary<Cell, int> { [map.Start] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(map.Start);

        while (queue.Count > 0)
        {
            Cell current = queue.Dequeue();
            if (current == map.Goal) return distance[current];

            foreach (Cell next in map.Neighbours(current))
            {
                if (map.IsWall(next) || distance.ContainsKey(next)) continue;

                distance[next] = distance[current] + 1;
                queue.Enqueue(next);
            }
        }

        return int.MaxValue;
    }

    private static bool IsReachable(GridMap map)
    {
        if (map.IsWall(map.Start) || map.IsWall(map.Goal)) return false;

        var visited = new HashSet<Cell> { map.Start };
        var queue = new Queue<Cell>();
        queue.Enqueue(map.Start);

        while (queue.Count > 0)
        {
            Cell current = queue.Dequeue();
            if (current == map.Goal) return true;

            foreach (Cell next in map.Neighbours(current))
            {
                if (map.IsWall(next) || !visited.Add(next)) continue;
                queue.Enqueue(next);
            }
        }

        return false;
    }

    /// <summary>
    /// Independent oracle for the radius candidates: own BFS for L, own reachability set,
    /// and the same integer band test, compared cell by cell and in order.
    /// </summary>
    private static bool MatchesRadiusCandidates(GridMap map, Cell entrance, double alpha,
        CandidateTargetsResult result)
    {
        if (!result.Success) return false;

        GridMap probe = map.Clone();
        probe.Goal = entrance;
        int steps = ReferenceSteps(probe);
        if (steps == int.MaxValue) return false;

        int radius = (int)Math.Floor(steps * alpha);
        if (result.BasePathSteps != steps || result.Radius != radius) return false;

        var reachable = new HashSet<Cell>();
        if (!map.IsWall(entrance))
        {
            reachable.Add(entrance);
            var queue = new Queue<Cell>();
            queue.Enqueue(entrance);

            while (queue.Count > 0)
            {
                Cell current = queue.Dequeue();
                foreach (Cell next in map.Neighbours(current))
                {
                    if (map.IsWall(next) || !reachable.Add(next)) continue;
                    queue.Enqueue(next);
                }
            }
        }

        var expected = new List<Cell>();
        long squaredLow = (long)result.MinRadius * result.MinRadius;
        long squaredHigh = (long)result.MaxRadius * result.MaxRadius;

        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            var cell = new Cell(x, y);
            if (map.IsWall(cell) || !reachable.Contains(cell)) continue;

            long dx = (long)x - entrance.X;
            long dy = (long)y - entrance.Y;
            long squared = dx * dx + dy * dy;
            if (squared < squaredLow || squared > squaredHigh) continue;

            expected.Add(cell);
        }

        return result.Candidates.SequenceEqual(expected);
    }

    /// <summary>Independent reverse-BFS oracle for the farthest-point module.</summary>
    private static bool MatchesReferenceDistances(GridMap map, Cell target, FarthestPointResult result)
    {
        var reference = new Dictionary<Cell, int>();
        var queue = new Queue<Cell>();

        if (map.IsWall(target))
        {
            // Wall reference: every open neighbour is one step out of the obstacle.
            foreach (Cell neighbour in map.Neighbours(target))
            {
                if (map.IsWall(neighbour)) continue;
                reference[neighbour] = 1;
                queue.Enqueue(neighbour);
            }
        }
        else
        {
            reference[target] = 0;
            queue.Enqueue(target);
        }

        while (queue.Count > 0)
        {
            Cell current = queue.Dequeue();
            foreach (Cell next in map.Neighbours(current))
            {
                if (map.IsWall(next) || reference.ContainsKey(next)) continue;
                reference[next] = reference[current] + 1;
                queue.Enqueue(next);
            }
        }

        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            var cell = new Cell(x, y);
            int expected = reference.TryGetValue(cell, out int value) ? value : FarthestPointResult.Unreachable;
            if (result.DistanceAt(cell) != expected) return false;
        }

        return true;
    }
}
